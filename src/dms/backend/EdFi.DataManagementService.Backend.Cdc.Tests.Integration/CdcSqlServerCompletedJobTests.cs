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
            (_, _) => Task.FromResult(true),
            (_, boundary, token) =>
            {
                boundary.Should().Be(0);
                if (++reads == 1)
                {
                    return Task.FromResult(new CdcSqlServerJobExecution(Job, 0, -1));
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
            completion.SetResult(new(Job, 2, 1));
            (await operation).Execution.InstanceId.Should().Be(2);
            reads.Should().Be(2);
        }
        finally
        {
            await caller.CancelAsync();
            completion.TrySetResult(new(Job, 2, 1));
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
                return Task.FromResult(started);
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
            completion.SetResult(new(Job, 9, 1));
            var evidence = await operation;
            evidence.HistoryBoundary.Should().Be(initiallyRunning ? 7 : 0);
            evidence.Execution.Should().Be(new CdcSqlServerJobExecution(Job, 9, 1));
            evidence.Started.Should().Be(started);
        }
        finally
        {
            await cancellation.CancelAsync();
            completion.TrySetResult(new(Job, 9, 1));
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
                (_, _) => Task.FromResult(true),
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
                            scenario == "failed" ? 0 : 1
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

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_distinguishes_cancellation_from_a_completion_deadline(bool callerCancels)
    {
        var clock = new FakeTimeProvider();
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = CdcSqlServerCompletedJob.RunAsync(
            new(Job, 0, 0, false),
            _ => throw new AssertionException("Unexpected pre-start read."),
            (_, _) => Task.FromResult(true),
            async (_, _, token) =>
            {
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
