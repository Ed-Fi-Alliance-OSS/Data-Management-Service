// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
public sealed class Given_CdcFixtureProbes
{
    private static readonly TimeSpan ObservationBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(20);

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_cancels_and_awaits_probe_exit_before_reporting_failure_or_disposing_resources(
        bool callerCancels
    )
    {
        var clock = new DrainClock();
        ConcurrentQueue<CdcProbeFailureEvidence> evidence = new();
        var owner = new CdcFixtureProbes(
            clock,
            DrainBudget,
            item =>
            {
                evidence.Enqueue(item);
                return Task.CompletedTask;
            }
        );
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entered = Signal();
        var cancelled = Signal();
        var exit = Signal();
        int calls = 0;
        int writes = 0;
        bool disposed = false;
        var operation = owner.WaitAsync(
            async token =>
            {
                calls++;
                entered.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    cancelled.SetResult();
                    await exit.Task;
                    disposed.Should().BeFalse();
                    writes++;
                }
                return false;
            },
            ObservationBudget,
            TimeSpan.FromSeconds(1),
            caller.Token
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
                clock.Advance(ObservationBudget);
            }
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await clock.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            operation.IsCompleted.Should().BeFalse();
            evidence.Should().BeEmpty("the probe can still mutate state until its exit");
            exit.SetResult();
            Func<Task> finish = () => operation;
            if (callerCancels)
            {
                await finish.Should().ThrowAsync<OperationCanceledException>();
            }
            else
            {
                await finish.Should().ThrowAsync<TimeoutException>();
            }
            writes.Should().Be(1);
            calls.Should().Be(1);
            evidence
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    new CdcProbeFailureEvidence(
                        callerCancels ? "CallerCancellation" : "ObservationTimeout",
                        "None",
                        "Cancelled"
                    )
                );
            await owner.DisposeResourcesAsync(() =>
            {
                disposed = true;
                return Task.CompletedTask;
            });
            writes.Should().Be(1);
        }
        finally
        {
            exit.TrySetResult();
            await caller.CancelAsync();
            await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task It_retains_late_work_and_resources_after_failed_drain_without_mutating_published_evidence(
        bool callerCancels,
        bool lateFault
    )
    {
        var clock = new DrainClock();
        ConcurrentQueue<CdcProbeFailureEvidence> evidence = new();
        var owner = new CdcFixtureProbes(
            clock,
            DrainBudget,
            item =>
            {
                evidence.Enqueue(item);
                return Task.CompletedTask;
            }
        );
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entered = Signal();
        var cancelled = Signal();
        var exit = Signal();
        bool disposed = false;
        int calls = 0;
        var operation = owner.WaitAsync(
            async token =>
            {
                calls++;
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult();
                await exit.Task;
                disposed.Should().BeFalse();
                if (lateFault)
                {
                    throw new InvalidOperationException("private late failure");
                }
                return true;
            },
            ObservationBudget,
            TimeSpan.FromSeconds(1),
            caller.Token
        );
        Task cleanup = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(caller.Token);
            if (callerCancels)
            {
                await caller.CancelAsync();
            }
            else
            {
                clock.Advance(ObservationBudget);
            }
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await clock.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            clock.Advance(DrainBudget);
            Func<Task> finish = () => operation;
            var failure = (await finish.Should().ThrowAsync<CdcProbeDrainException>()).Which;
            if (callerCancels)
            {
                failure.InnerExceptions[0].Should().BeAssignableTo<OperationCanceledException>();
            }
            else
            {
                failure.InnerExceptions[0].Should().BeOfType<TimeoutException>();
            }
            owner.DrainFailed.Should().BeTrue();
            string snapshot = JsonSerializer.Serialize(evidence.ToArray());
            snapshot.Should().NotContain("private");
            evidence
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    new CdcProbeFailureEvidence(
                        callerCancels ? "CallerCancellation" : "ObservationTimeout",
                        "Timeout",
                        "Pending"
                    )
                );
            var parent = new CdcFixtureProbes(publish: _ => Task.CompletedTask);
            Func<Task> dispose = () =>
                parent.DisposeResourcesAsync(() =>
                    owner.DisposeResourcesAsync(() =>
                    {
                        disposed = true;
                        return Task.CompletedTask;
                    })
                );
            cleanup = (await dispose.Should().ThrowAsync<CdcProbeDrainException>()).Which.Completion;
            disposed.Should().BeFalse();
            cleanup.IsCompleted.Should().BeFalse();
            exit.SetResult();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(30));
            disposed.Should().BeTrue();
            calls.Should().Be(1);
            owner.LateOutcomes.Should().Equal(lateFault ? "Faulted" : "Completed");
            JsonSerializer.Serialize(evidence.ToArray()).Should().Be(snapshot);
            Func<Task> anotherProbe = () =>
                owner.WaitAsync(
                    _ => throw new AssertionException("Must not start"),
                    ObservationBudget,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None
                );
            await anotherProbe.Should().ThrowAsync<ObjectDisposedException>();
            await finish.Should().ThrowAsync<CdcProbeDrainException>();
        }
        finally
        {
            exit.TrySetResult();
            await caller.CancelAsync();
            await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await cleanup;
        }
    }

    [Test]
    public async Task It_does_not_invoke_a_probe_after_caller_cancellation()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        var owner = new CdcFixtureProbes(publish: _ => Task.CompletedTask);
        Func<Task> operation = () =>
            owner.WaitAsync(
                _ => throw new AssertionException("Must not start"),
                ObservationBudget,
                TimeSpan.FromSeconds(1),
                caller.Token
            );
        await operation.Should().ThrowAsync<OperationCanceledException>();
        owner.DrainFailed.Should().BeFalse();
    }

    [Test]
    public async Task It_preserves_a_probe_failure_and_stops_polling()
    {
        var original = new InvalidOperationException("private source");
        var owner = new CdcFixtureProbes(publish: _ => Task.CompletedTask);
        int calls = 0;
        Func<Task> operation = () =>
            owner.WaitAsync(
                _ =>
                {
                    calls++;
                    throw original;
                },
                ObservationBudget,
                TimeSpan.FromSeconds(1),
                CancellationToken.None
            );
        (await operation.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(original);
        calls.Should().Be(1);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_propagates_nested_drain_failure_without_waiting_and_retains_remaining_dependencies(
        bool lateCleanupFails
    )
    {
        var owner = new CdcFixtureProbes(publish: _ => Task.CompletedTask);
        var childExit = Signal();
        var grandchildExit = Signal();
        var secondCleanup = Signal();
        List<string> calls = [];
        Task deferred = Task.CompletedTask;
        try
        {
            Task disposal = owner.DisposeResourcesAsync(() =>
                owner.DisposeInOrderAsync(
                    () =>
                    {
                        calls.Add("child");
                        throw new CdcProbeDrainException(new TimeoutException(), childExit.Task);
                    },
                    () =>
                    {
                        calls.Add("second-child");
                        secondCleanup.TrySetResult();
                        throw new CdcProbeDrainException(new TimeoutException(), grandchildExit.Task);
                    },
                    () =>
                    {
                        calls.Add("dependency");
                        return lateCleanupFails
                            ? Task.FromException(new InvalidOperationException("private late failure"))
                            : Task.CompletedTask;
                    }
                )
            );
            // No timer: both children are deliberately still blocked when teardown must return.
            disposal.IsCompleted.Should().BeTrue();
            Func<Task> finish = () => disposal;
            deferred = (await finish.Should().ThrowAsync<CdcProbeDrainException>()).Which.Completion;
            deferred.IsCompleted.Should().BeFalse();
            calls.Should().ContainSingle().Which.Should().Be("child");
            childExit.SetResult();
            await secondCleanup.Task.WaitAsync(TimeSpan.FromSeconds(30));
            deferred.IsCompleted.Should().BeFalse();
            calls.Should().Equal("child", "second-child");
            grandchildExit.SetResult();
            await deferred.WaitAsync(TimeSpan.FromSeconds(30));
            calls.Should().Equal("child", "second-child", "dependency");
            if (lateCleanupFails)
            {
                owner.LateOutcomes.Should().ContainSingle().Which.Should().Be("CleanupFaulted");
            }
            else
            {
                owner.LateOutcomes.Should().BeEmpty();
            }
            await finish.Should().ThrowAsync<CdcProbeDrainException>();
        }
        finally
        {
            childExit.TrySetResult();
            grandchildExit.TrySetResult();
            await deferred;
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DrainClock : FakeTimeProvider
    {
        internal TaskCompletionSource DrainStarted { get; } = Signal();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == DrainBudget)
            {
                DrainStarted.TrySetResult();
            }
            return timer;
        }
    }
}
