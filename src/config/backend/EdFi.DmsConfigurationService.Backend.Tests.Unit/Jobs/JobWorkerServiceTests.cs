// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

/// <summary>A job row as <see cref="SimulatedJobTable"/> keeps it.</summary>
public sealed class SimulatedJob(long id)
{
    public long Id { get; } = id;
    public string JobId => $"job-{Id}";
    public string Status { get; set; } = JobStatuses.Pending;
    public int AttemptCount { get; set; }
    public long FencingToken { get; set; }
    public string? LeaseOwner { get; set; }
    public string? ErrorCode { get; set; }
}

/// <summary>
/// A lease repository that behaves like the table for the worker's purposes: claims take pending rows under the
/// limit in order, outcome writes move rows as D-4/D-6 do, and <c>Exhaust</c> ends pending rows at the limit.
/// </summary>
public sealed class SimulatedJobTable : IJobLeaseRepository
{
    private readonly object _sync = new();

    public List<SimulatedJob> Rows { get; } = [];

    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>The next this many claims throw, as a lost connection would.</summary>
    public int ClaimsThatThrow { get; set; }

    public SimulatedJob Add(int attemptCount = 0)
    {
        lock (_sync)
        {
            SimulatedJob row = new(Rows.Count + 1)
            {
                AttemptCount = attemptCount,
                FencingToken = attemptCount,
            };
            Rows.Add(row);
            return row;
        }
    }

    public Task<JobClaimResult> ClaimNext(
        string owner,
        int leaseSeconds,
        int maxAttempts,
        CancellationToken cancellationToken
    )
    {
        Calls.Enqueue("ClaimNext");
        lock (_sync)
        {
            if (ClaimsThatThrow > 0)
            {
                ClaimsThatThrow--;
                throw new InvalidOperationException("The connection was lost.");
            }

            SimulatedJob? row = Rows.Find(candidate =>
                candidate.Status == JobStatuses.Pending && candidate.AttemptCount < maxAttempts
            );
            if (row is null)
            {
                return Task.FromResult<JobClaimResult>(new JobClaimResult.NoneAvailable());
            }

            row.Status = JobStatuses.InProgress;
            row.AttemptCount++;
            row.FencingToken++;
            row.LeaseOwner = owner;
            DateTime now = DateTime.UtcNow;
            return Task.FromResult<JobClaimResult>(
                new JobClaimResult.Claimed(
                    new ClaimedJob(
                        row.Id,
                        row.JobId,
                        null,
                        ExecutorHarness.JobType,
                        1,
                        """{"code":"a","number":1}""",
                        row.AttemptCount,
                        row.FencingToken,
                        owner,
                        now.AddSeconds(leaseSeconds),
                        now,
                        now,
                        now,
                        false
                    )
                )
            );
        }
    }

    public Task<JobExhaustResult> Exhaust(
        int maxAttempts,
        JobErrorCode errorCode,
        CancellationToken cancellationToken
    )
    {
        Calls.Enqueue("Exhaust");
        lock (_sync)
        {
            int exhausted = 0;
            foreach (
                SimulatedJob row in Rows.Where(row =>
                    row.Status == JobStatuses.Pending && row.AttemptCount >= maxAttempts
                )
            )
            {
                row.Status = JobStatuses.Error;
                row.ErrorCode = errorCode.Code;
                row.FencingToken++;
                exhausted++;
            }
            return Task.FromResult<JobExhaustResult>(new JobExhaustResult.Success(exhausted));
        }
    }

    public Task<JobWriteResult> Renew(
        long id,
        string owner,
        long fencingToken,
        int leaseSeconds,
        CancellationToken cancellationToken
    ) => Write(id, owner, fencingToken, _ => { });

    public Task<JobWriteResult> Complete(
        long id,
        string owner,
        long fencingToken,
        CancellationToken cancellationToken
    ) => Write(id, owner, fencingToken, row => row.Status = JobStatuses.Completed);

    public Task<JobWriteResult> FailTransient(
        long id,
        string owner,
        long fencingToken,
        int backoffSeconds,
        CancellationToken cancellationToken
    ) => Write(id, owner, fencingToken, row => row.Status = JobStatuses.Pending);

    public Task<JobWriteResult> FailTerminal(
        long id,
        string owner,
        long fencingToken,
        JobErrorCode errorCode,
        CancellationToken cancellationToken
    ) =>
        Write(
            id,
            owner,
            fencingToken,
            row =>
            {
                row.Status = JobStatuses.Error;
                row.ErrorCode = errorCode.Code;
            }
        );

    public Task<JobWriteResult> ReleaseToPending(
        long id,
        string owner,
        long fencingToken,
        CancellationToken cancellationToken
    )
    {
        Calls.Enqueue("ReleaseToPending");
        return Write(id, owner, fencingToken, row => row.Status = JobStatuses.Pending);
    }

    private Task<JobWriteResult> Write(long id, string owner, long fencingToken, Action<SimulatedJob> change)
    {
        lock (_sync)
        {
            SimulatedJob row = Rows.Single(candidate => candidate.Id == id);
            if (
                row.Status != JobStatuses.InProgress
                || row.LeaseOwner != owner
                || row.FencingToken != fencingToken
            )
            {
                return Task.FromResult<JobWriteResult>(new JobWriteResult.OwnershipLost());
            }

            change(row);
            if (row.Status != JobStatuses.InProgress)
            {
                row.LeaseOwner = null;
            }
            return Task.FromResult<JobWriteResult>(new JobWriteResult.Success(null, DateTime.UtcNow));
        }
    }
}

/// <summary>A worker over a <see cref="SimulatedJobTable"/>, with the real executor and a fake clock.</summary>
public sealed class WorkerHarness : IDisposable
{
    private readonly ExecutorHarness _executor;

    public WorkerHarness(SimulatedJobTable? table = null, int maxConcurrentJobs = 2, int maxAttempts = 5)
    {
        Table = table ?? new SimulatedJobTable();
        _executor = new ExecutorHarness(leaseRepository: Table);
        _executor.Settings.MaxConcurrentJobs = maxConcurrentJobs;
        _executor.Settings.MaxAttempts = maxAttempts;
        _executor.Script.Run = async (context, _, token) =>
        {
            int now = Interlocked.Increment(ref _concurrent);
            InterlockedMax(ref _maximumConcurrent, now);
            Started.Enqueue(context.JobId);
            try
            {
                await Handler(context, token);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        };
        Worker = new JobWorkerService(
            Table,
            _executor.Executor,
            Options.Create(_executor.Settings),
            _executor.Time,
            Logger
        );
    }

    private int _concurrent;
    private int _maximumConcurrent;

    public SimulatedJobTable Table { get; }
    public JobWorkerService Worker { get; }
    public CapturingLogger<JobWorkerService> Logger { get; } = new();
    public ConcurrentQueue<string> Started { get; } = new();
    public int MaximumConcurrent => Volatile.Read(ref _maximumConcurrent);
    public TimeSpan PollInterval => _executor.Settings.PollInterval;

    /// <summary>What each execution's handler does; by default it runs until it is cancelled.</summary>
    public Func<JobExecutionContext, CancellationToken, Task> Handler { get; set; } =
        (_, token) => Task.Delay(Timeout.Infinite, token);

    public void Advance(TimeSpan by) => _executor.Time.Advance(by);

    /// <summary>Stops the worker, failing rather than hanging when an execution does not end.</summary>
    public async Task StopAsync()
    {
        using CancellationTokenSource giveUp = new(TimeSpan.FromSeconds(10));
        await Worker.StopAsync(giveUp.Token);
        if (!Worker.ExecuteTask!.IsCompleted)
        {
            throw new TimeoutException("The worker did not stop within 10 seconds.");
        }
    }

    public static async Task Until(Func<bool> condition, string because)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException($"Timed out waiting until {because}.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    /// <summary>
    /// Advances the clock one poll interval at a time until <paramref name="condition"/> holds. The worker may not have
    /// registered its next delay yet when a poll is observed, so a single advance could be lost; each advance fires at
    /// most the one pending delay, so polls are never skipped or doubled.
    /// </summary>
    public async Task AdvanceUntil(Func<bool> condition, string because)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException($"Timed out advancing until {because}.");
            }
            Advance(PollInterval);
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    public void Dispose()
    {
        Worker.Dispose();
        _executor.Dispose();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}

public class JobWorkerServiceTests
{
    [TestFixture]
    public class Given_more_pending_jobs_than_the_concurrency_limit
    {
        private WorkerHarness _harness = null!;
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _finish = new();
        private int _claimsAtCapacity;

        [SetUp]
        public async Task Setup()
        {
            _finish.Clear();
            _harness = new WorkerHarness(maxConcurrentJobs: 2);
            for (int job = 0; job < 5; job++)
            {
                _harness.Table.Add();
            }
            _harness.Handler = (context, token) =>
                _finish
                    .GetOrAdd(context.JobId, _ => ScriptedLeaseRepository.NewSignal())
                    .Task.WaitAsync(token);

            await _harness.Worker.StartAsync(CancellationToken.None);
            await WorkerHarness.Until(() => _harness.Started.Count == 2, "two executions started");
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            _claimsAtCapacity = _harness.Table.Calls.Count(call => call == "ClaimNext");

            _finish.GetOrAdd("job-1", _ => ScriptedLeaseRepository.NewSignal()).SetResult();
            await WorkerHarness.Until(() => _harness.Started.Count == 3, "a third execution started");
        }

        [TearDown]
        public async Task TearDown()
        {
            await _harness.StopAsync();
            _harness.Dispose();
        }

        [Test]
        public void It_claims_up_to_the_limit_then_waits() => _claimsAtCapacity.Should().Be(2);

        [Test]
        public void It_claims_again_as_soon_as_an_execution_finishes()
        {
            // The first two run concurrently, so their handlers may start in either order.
            _harness.Started.Take(2).Should().BeEquivalentTo("job-1", "job-2");
            _harness.Started.ElementAt(2).Should().Be("job-3");
            _harness.MaximumConcurrent.Should().Be(2);
        }
    }

    [TestFixture]
    public class Given_the_host_stopping_with_running_executions
    {
        private WorkerHarness _harness = null!;
        private int _claimsBeforeStop;

        [SetUp]
        public async Task Setup()
        {
            _harness = new WorkerHarness(maxConcurrentJobs: 2);
            _harness.Table.Add();
            _harness.Table.Add();
            _harness.Table.Add();

            await _harness.Worker.StartAsync(CancellationToken.None);
            await WorkerHarness.Until(() => _harness.Started.Count == 2, "two executions started");
            _claimsBeforeStop = _harness.Table.Calls.Count(call => call == "ClaimNext");
            await _harness.StopAsync();
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_stops_claiming_and_releases_owned_jobs_on_shutdown()
        {
            _harness.Table.Calls.Count(call => call == "ClaimNext").Should().Be(_claimsBeforeStop);
            _harness.Table.Calls.Count(call => call == "ReleaseToPending").Should().Be(2);
            _harness
                .Table.Rows.Take(2)
                .Should()
                .OnlyContain(row => row.Status == JobStatuses.Pending && row.AttemptCount == 1);
        }

        [Test]
        public void It_waits_for_every_execution_before_it_stops() =>
            _harness.Worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_job_released_on_its_final_attempt
    {
        private WorkerHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new WorkerHarness(maxAttempts: 3);
            _harness.Table.Add(attemptCount: 3);

            await _harness.Worker.StartAsync(CancellationToken.None);
            await WorkerHarness.Until(
                () => _harness.Table.Calls.Contains("ClaimNext"),
                "the first poll claimed"
            );
            await _harness.StopAsync();
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_terminates_released_final_attempt_on_next_sweep()
        {
            _harness
                .Table.Rows.Single()
                .Should()
                .Match<SimulatedJob>(row =>
                    row.Status == JobStatuses.Error && row.ErrorCode == JobErrorCode.AttemptsExhausted.Code
                );
            _harness.Started.Should().BeEmpty();
        }

        [Test]
        public void It_logs_the_sweep() =>
            _harness.Logger.Entries.Should().Contain(entry => entry.EventId.Name == "JobsExhausted");
    }

    [TestFixture]
    public class Given_repeated_shutdowns_during_one_job
    {
        private readonly List<int> _attemptsAfterEachShutdown = [];
        private SimulatedJobTable _table = null!;
        private int _executionsAfterTheLimit;

        [SetUp]
        public async Task Setup()
        {
            _attemptsAfterEachShutdown.Clear();
            _table = new SimulatedJobTable();
            _table.Add();
            for (int shutdown = 0; shutdown < 3; shutdown++)
            {
                using WorkerHarness harness = new(_table, maxAttempts: 3);
                await harness.Worker.StartAsync(CancellationToken.None);
                await WorkerHarness.Until(() => harness.Started.Count == 1, "the execution started");
                await harness.StopAsync();
                _attemptsAfterEachShutdown.Add(_table.Rows.Single().AttemptCount);
            }

            using WorkerHarness last = new(_table, maxAttempts: 3);
            await last.Worker.StartAsync(CancellationToken.None);
            await WorkerHarness.Until(
                () => _table.Calls.Count(call => call == "Exhaust") >= 4,
                "the next sweep ran"
            );
            await last.StopAsync();
            _executionsAfterTheLimit = last.Started.Count;
        }

        [Test]
        public void It_keeps_attempt_count_across_repeated_shutdowns() =>
            _attemptsAfterEachShutdown.Should().Equal(1, 2, 3);

        [Test]
        public void It_exhausts_the_job_once_every_attempt_was_used()
        {
            _table.Rows.Single().Status.Should().Be(JobStatuses.Error);
            _executionsAfterTheLimit.Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_a_claim_that_throws
    {
        private WorkerHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new WorkerHarness();
            _harness.Table.ClaimsThatThrow = 1;
            _harness.Table.Add();
            _harness.Handler = (_, _) => Task.CompletedTask;

            await _harness.Worker.StartAsync(CancellationToken.None);
            await WorkerHarness.Until(
                () => _harness.Logger.Entries.Any(entry => entry.EventId.Name == "WorkerPollFailed"),
                "the failed poll was logged"
            );
            await _harness.AdvanceUntil(
                () => _harness.Table.Rows.Single().Status == JobStatuses.Completed,
                "the job completed on the next poll"
            );
        }

        [TearDown]
        public async Task TearDown()
        {
            await _harness.StopAsync();
            _harness.Dispose();
        }

        [Test]
        public void It_does_not_fault_the_service() =>
            _harness.Worker.ExecuteTask!.IsFaulted.Should().BeFalse();

        [Test]
        public void It_logs_the_failure_by_type_chain_only() =>
            _harness
                .Logger.Entries.Single(entry => entry.EventId.Name == "WorkerPollFailed")
                .Should()
                .Match<LogEntry>(entry =>
                    (string?)entry.Field("ExceptionTypeChain") == "System.InvalidOperationException"
                    && !entry.Message.Contains("connection was lost")
                );
    }

    [TestFixture]
    public class Given_an_idle_queue
    {
        private WorkerHarness _harness = null!;
        private int _sweeps;

        [SetUp]
        public async Task Setup()
        {
            _harness = new WorkerHarness();
            await _harness.Worker.StartAsync(CancellationToken.None);
            await WorkerHarness.Until(() => _harness.Table.Calls.Count == 2, "the first poll ran");
            await _harness.AdvanceUntil(() => _harness.Table.Calls.Count == 4, "the second poll ran");
            await _harness.AdvanceUntil(() => _harness.Table.Calls.Count == 6, "the third poll ran");
            _sweeps = _harness.Table.Calls.Count(call => call == "Exhaust");
        }

        [TearDown]
        public async Task TearDown()
        {
            await _harness.StopAsync();
            _harness.Dispose();
        }

        [Test]
        public void It_sweeps_and_claims_once_per_poll()
        {
            _sweeps.Should().Be(3);
            _harness
                .Table.Calls.Should()
                .Equal("Exhaust", "ClaimNext", "Exhaust", "ClaimNext", "Exhaust", "ClaimNext");
        }
    }
}
