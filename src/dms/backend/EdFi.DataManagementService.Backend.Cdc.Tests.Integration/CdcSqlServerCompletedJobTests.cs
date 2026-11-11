// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
public sealed class Given_CdcSqlServerCompletedJob
{
    private static readonly Guid Job = Guid.Parse("12345678-1234-1234-1234-123456789abc");

    private static readonly DateTime RequestedAt = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Unspecified);

    private static CdcSqlServerJobExecution Execution(int id, int status = 1) =>
        new(Job, id, status, 3, RequestedAt, 4);

    [Test]
    public async Task It_treats_empty_completed_history_as_pending_until_the_requested_job_finishes()
    {
        var clock = new FakeTimeProvider();
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<CdcSqlServerJobExecution>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int reads = 0;
        var operation = CdcSqlServerCompletedJob.RunAsync(
            new(Job, 0, 0, false),
            _ => throw new AssertionException("Unexpected pre-start read."),
            (_, _) => Task.FromResult(new CdcSqlServerJobRequest(true, 3, RequestedAt)),
            (_, boundary, token) =>
            {
                boundary.Should().Be(0);
                if (++reads == 1)
                {
                    return Task.FromResult(Execution(0, -1));
                }
                secondRead.TrySetResult();
                return completion.Task.WaitAsync(token);
            },
            caller.Token,
            clock
        );
        try
        {
            reads.Should().Be(1);
            operation.IsCompleted.Should().BeFalse();
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await secondRead.Task.WaitAsync(caller.Token);
            operation.IsCompleted.Should().BeFalse();
            completion.SetResult(Execution(2));
            (await operation).Execution.InstanceId.Should().Be(2);
            reads.Should().Be(2);
        }
        finally
        {
            await caller.CancelAsync();
            completion.TrySetResult(Execution(2));
            await ((Task)operation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(false, true)]
    [TestCase(true, true)]
    [TestCase(false, false)]
    public async Task It_requires_a_new_successful_execution_of_the_fixture_job(
        bool initiallyRunning,
        bool started
    )
    {
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<CdcSqlServerJobExecution>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        List<string> calls = [];
        var operation = CdcSqlServerCompletedJob.RunAsync(
            new(Job, 0, 0, initiallyRunning),
            _ =>
            {
                calls.Add("idle");
                return Task.FromResult(new CdcSqlServerJobState(Job, 7, 1, false));
            },
            (job, _) =>
            {
                job.Should().Be(Job);
                calls.Add("start");
                return Task.FromResult(new CdcSqlServerJobRequest(started, 3, RequestedAt));
            },
            (job, boundary, token) =>
            {
                job.Should().Be(Job);
                boundary.Should().Be(initiallyRunning ? 7 : 0);
                calls.Add("history");
                readEntered.TrySetResult();
                return completion.Task.WaitAsync(token);
            },
            cancellation.Token,
            clock
        );
        try
        {
            if (initiallyRunning)
            {
                calls.Should().BeEmpty("an active execution is not completed history");
                clock.Advance(TimeSpan.FromMilliseconds(250));
            }
            await readEntered.Task.WaitAsync(cancellation.Token);
            operation.IsCompleted.Should().BeFalse();
            string[] expected = initiallyRunning ? ["idle", "start", "history"] : ["start", "history"];
            calls.Should().Equal(expected);
            var execution = Execution(9) with { RequestSource = started ? 4 : 1 };
            completion.SetResult(execution);
            var evidence = await operation;
            evidence.HistoryBoundary.Should().Be(initiallyRunning ? 7 : 0);
            evidence.Execution.Should().Be(execution);
            evidence.Started.Should().Be(started);
        }
        finally
        {
            await cancellation.CancelAsync();
            completion.TrySetResult(Execution(9));
            await ((Task)operation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(true, "scheduled")]
    [TestCase(true, "old-request")]
    [TestCase(true, "old-session")]
    [TestCase(false, "scheduled")]
    [TestCase(false, "old-session")]
    public async Task It_does_not_accept_an_intervening_execution_as_the_requested_or_joined_run(
        bool started,
        string scenario
    )
    {
        var clock = new FakeTimeProvider();
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var finalRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<CdcSqlServerJobExecution>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int reads = 0;
        var operation = CdcSqlServerCompletedJob.RunAsync(
            new(Job, 7, 1, false),
            _ => throw new AssertionException("Unexpected pre-start read."),
            (_, _) => Task.FromResult(new CdcSqlServerJobRequest(started, 3, RequestedAt)),
            (_, boundary, token) =>
            {
                boundary.Should().Be(7);
                if (++reads == 1)
                {
                    // Execution 8 completed after the snapshot, before execution 9 was
                    // started/joined. It must not certify 9 even when its history is successful.
                    return Task.FromResult(
                        Execution(8) with
                        {
                            SessionId = scenario == "old-session" ? 2 : 3,
                            RequestedAt =
                                scenario == "scheduled" && started ? RequestedAt : RequestedAt.AddSeconds(-1),
                            RequestSource = scenario == "scheduled" ? 1 : 4,
                        }
                    );
                }
                finalRead.TrySetResult();
                return completion.Task.WaitAsync(token);
            },
            caller.Token,
            clock
        );
        try
        {
            reads.Should().Be(1);
            operation.IsCompleted.Should().BeFalse();
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await finalRead.Task.WaitAsync(caller.Token);
            operation.IsCompleted.Should().BeFalse();
            // A later failure of the requested execution must not be hidden by run 8's success.
            completion.SetResult(Execution(9, 0));
            Func<Task> finish = () => operation;
            await finish.Should().ThrowAsync<InvalidOperationException>();
            reads.Should().Be(2);
        }
        finally
        {
            await caller.CancelAsync();
            completion.TrySetResult(Execution(9));
            await ((Task)operation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task It_retries_when_the_rejected_execution_finishes_before_it_can_be_joined()
    {
        var clock = new FakeTimeProvider();
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        List<string> calls = [];
        var operation = CdcSqlServerCompletedJob.RunAsync(
            new(Job, 7, 1, false),
            _ => throw new AssertionException("Unexpected pre-start read."),
            (job, _) =>
            {
                job.Should().Be(Job);
                calls.Add("start");
                // The first start was rejected as already running, then the active-row
                // lookup found nothing because that execution had just completed.
                return Task.FromResult(
                    calls.Count == 1
                        ? CdcSqlServerJobRequest.Retry
                        : new CdcSqlServerJobRequest(true, 3, RequestedAt)
                );
            },
            (job, boundary, _) =>
            {
                job.Should().Be(Job);
                boundary.Should().Be(7);
                calls.Add("history");
                return Task.FromResult(Execution(9));
            },
            caller.Token,
            clock
        );
        try
        {
            calls.Should().Equal("start");
            operation.IsCompleted.Should().BeFalse();
            clock.Advance(TimeSpan.FromMilliseconds(250));
            var evidence = await operation.WaitAsync(caller.Token);
            calls.Should().Equal("start", "start", "history");
            evidence.HistoryBoundary.Should().Be(7);
            evidence.Started.Should().BeTrue();
            evidence.Execution.Should().Be(Execution(9));
        }
        finally
        {
            await caller.CancelAsync();
            await ((Task)operation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase("failed")]
    [TestCase("old")]
    [TestCase("other-job")]
    [TestCase("denied")]
    public async Task It_rejects_failed_stale_unrelated_and_unavailable_history(string scenario)
    {
        int reads = 0;
        Func<Task> operation = () =>
            CdcSqlServerCompletedJob.RunAsync(
                new(Job, 7, 1, false),
                _ => throw new AssertionException("Idle job needs no additional pre-start read."),
                (_, _) => Task.FromResult(new CdcSqlServerJobRequest(true, 3, RequestedAt)),
                (_, _, _) =>
                {
                    reads++;
                    if (scenario == "denied")
                    {
                        return Task.FromException<CdcSqlServerJobExecution>(
                            new UnauthorizedAccessException()
                        );
                    }
                    return Task.FromResult(
                        new CdcSqlServerJobExecution(
                            scenario == "other-job" ? Guid.NewGuid() : Job,
                            scenario == "old" ? 7 : 9,
                            scenario == "failed" ? 0 : 1,
                            3,
                            RequestedAt,
                            4
                        )
                    );
                },
                CancellationToken.None
            );
        if (scenario == "denied")
        {
            await operation.Should().ThrowAsync<UnauthorizedAccessException>();
        }
        else
        {
            await operation.Should().ThrowAsync<InvalidOperationException>();
        }
        reads.Should().Be(1);
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task It_distinguishes_cancellation_from_a_completion_deadline(
        bool callerCancels,
        bool retryingStart
    )
    {
        var clock = new FakeTimeProvider();
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = CdcSqlServerCompletedJob.RunAsync(
            new(Job, 0, 0, false),
            _ => throw new AssertionException("Unexpected pre-start read."),
            (_, _) =>
            {
                if (retryingStart)
                {
                    entered.TrySetResult();
                    return Task.FromResult(CdcSqlServerJobRequest.Retry);
                }
                return Task.FromResult(new CdcSqlServerJobRequest(true, 3, RequestedAt));
            },
            async (_, _, token) =>
            {
                retryingStart.Should().BeFalse("no execution was started or joined yet");
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new AssertionException("Expected cancellation.");
            },
            caller.Token,
            clock
        );
        try
        {
            await entered.Task.WaitAsync(caller.Token);
            if (callerCancels)
            {
                await caller.CancelAsync();
            }
            else
            {
                clock.Advance(TimeSpan.FromMinutes(2));
            }
            Func<Task> finish = () => operation;
            if (callerCancels)
            {
                await finish.Should().ThrowAsync<OperationCanceledException>();
            }
            else
            {
                await finish.Should().ThrowAsync<TimeoutException>();
            }
        }
        finally
        {
            await caller.CancelAsync();
            await ((Task)operation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
