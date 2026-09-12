// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

internal partial class Given_CdcControllerStatus
{
    [Test]
    public async Task It_releases_the_lock_at_yield_and_observes_a_concurrent_stop_on_the_next_pass()
    {
        await using var watch = _status
            .WatchAsync([Selection()], 2, TimeSpan.FromMilliseconds(5))
            .GetAsyncEnumerator();
        (await watch.MoveNextAsync()).Should().BeTrue();
        watch.Current.Aggregate.Readiness.Should().Be(CdcReadiness.Ready);
        await using (
            var session = await _store.AcquireAsync(
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None
            )
        )
        {
            _stopped = true; // Concurrent lifecycle controller while watch is suspended outside its lock.
        }
        (await watch.MoveNextAsync()).Should().BeTrue();
        watch.Current.Aggregate.Readiness.Should().Be(CdcReadiness.NotReady);
        watch
            .Current.Targets.Single()
            .Status.ConnectorRuntime.State.Should()
            .Be(CdcComponentState.NotSatisfied);
        (await watch.MoveNextAsync()).Should().BeFalse();
        _stops.Should().Be(0);
    }

    [Test]
    public async Task It_releases_the_lock_during_the_watch_interval_and_preserves_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await using var watch = _status
            .WatchAsync([Selection()], 2, TimeSpan.FromMinutes(1), cancellation.Token)
            .GetAsyncEnumerator();
        (await watch.MoveNextAsync()).Should().BeTrue();
        var waiting = watch.MoveNextAsync().AsTask();
        await using (
            var session = await _store.AcquireAsync(
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None
            )
        )
        {
            waiting.IsCompleted.Should().BeFalse();
            await cancellation.CancelAsync();
        }
        Func<Task> act = () => waiting;
        await act.Should().ThrowAsync<OperationCanceledException>();
        _projectionReads.Should().Be(1);
    }

    [TestCase("provider")]
    [TestCase("offset")]
    [TestCase("worker")]
    [TestCase("config")]
    [TestCase("topic")]
    [TestCase("metrics")]
    public async Task It_preserves_cancellation_and_releases_the_lock_during_a_pass(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        _onCall = name =>
        {
            if (name == stage)
            {
                cancellation.Cancel();
            }
        };
        Func<Task> act = () => RunStatusAsync(cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(5),
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_holds_one_lock_through_terminal_persistence_and_containment()
    {
        _identity = new('b', 64);
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(async () =>
            {
                Func<Task> compete = async () =>
                {
                    await using var session = await _store.AcquireAsync(
                        TimeSpan.FromMilliseconds(25),
                        TimeSpan.FromMilliseconds(5),
                        CancellationToken.None
                    );
                };
                await compete.Should().ThrowAsync<CdcWorkflowStateException>();
                var state = await _bindings.ExactMatchBindingAsync(_request.Binding);
                state.State!.State.Should().Be(CdcBindingState.IncidentLatched);
                _stopped = true;
                return Observed(new CdcTransportAcknowledgement());
            });
        AssertContained(await TargetStatusAsync());
    }

    [Test]
    public async Task It_bounds_lock_contention_without_issuing_external_calls()
    {
        ShortTiming(40);
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(5),
            CancellationToken.None
        );
        var target = await TargetStatusAsync();
        target.Status.Readiness.Should().Be(CdcReadiness.NotReady);
        target.Diagnostics.Should().ContainSingle().Which.Failure.Should().Be(CdcDeploymentFailure.Timeout);
        _trace.Should().BeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_keeps_pending_rollouts_not_ready_across_watch_and_still_contains_loss(bool loss)
    {
        await using (
            var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None
            )
        )
        {
            var journal = await session.ReadAsync(Target, CancellationToken.None);
            int ceiling = _request.ConnectorPolicy.MaxRecordBytes;
            await session.RecordIntentAsync(
                Target,
                journal.WorkflowId,
                Guid.NewGuid(),
                CdcWorkflowEffect.IncreaseRecordSize,
                [
                    new(
                        _request.Binding.ToCompleteBindingIdentity(),
                        ceiling - 1,
                        ceiling,
                        [new(Guid.NewGuid(), "operator", DateTimeOffset.UtcNow, true, [])]
                    ),
                ],
                CancellationToken.None
            );
        }
        var before = await File.ReadAllBytesAsync(
            Directory
                .GetFiles(Path.Combine(_root, "workflows"), "*.json", SearchOption.AllDirectories)
                .Single()
        );
        if (loss)
        {
            _identity = new('b', 64);
        }
        int passes = 0;
        await foreach (var result in _status.WatchAsync([Selection()], 2, TimeSpan.FromMilliseconds(5)))
        {
            passes++;
            var target = result.Targets.Single();
            target.HasPendingRecordSizeIncrease.Should().BeTrue();
            target.Status.Readiness.Should().Be(CdcReadiness.NotReady);
            if (loss)
            {
                AssertContained(target);
            }
        }
        passes.Should().Be(2);
        (
            await File.ReadAllBytesAsync(
                Directory
                    .GetFiles(Path.Combine(_root, "workflows"), "*.json", SearchOption.AllDirectories)
                    .Single()
            )
        )
            .Should()
            .Equal(before);
        ResetStatus();
        (await TargetStatusAsync()).HasPendingRecordSizeIncrease.Should().BeTrue();
    }

    [Test]
    public async Task It_does_not_reuse_lag_evidence_between_passes()
    {
        await using var watch = _status
            .WatchAsync([Selection()], 2, TimeSpan.FromMilliseconds(5))
            .GetAsyncEnumerator();
        (await watch.MoveNextAsync()).Should().BeTrue();
        watch.Current.Aggregate.Readiness.Should().Be(CdcReadiness.Ready);
        _lag = 1001;
        (await watch.MoveNextAsync()).Should().BeTrue();
        watch.Current.Aggregate.Readiness.Should().Be(CdcReadiness.NotReady);
        watch.Current.Targets.Single().Details.LagMilliseconds.Should().Be(1001);
        _trace.Count(n => n == "metrics").Should().Be(2);
    }

    [Test]
    public async Task It_rejects_expired_telemetry_at_final_evaluation()
    {
        _onCall = name =>
        {
            if (name == "worker" && _trace.Contains("metrics"))
            {
                _telemetryClock.Advance(_request.Timing.MaximumObservationAge + TimeSpan.FromSeconds(1));
            }
        };
        (await TargetStatusAsync()).Status.Readiness.Should().NotBe(CdcReadiness.Ready);
    }

    [Test]
    public async Task It_rejects_empty_or_duplicate_target_inventories_without_mutation()
    {
        (await _status.StatusAsync([])).Aggregate.Readiness.Should().Be(CdcReadiness.Unknown);
        Func<Task> act = () => _status.StatusAsync([Selection(), Selection()]);
        await act.Should().ThrowAsync<ArgumentException>();
        _trace.Should().BeEmpty();
    }

    [TestCase(0, 1)]
    [TestCase(10001, 1)]
    [TestCase(1, 0)]
    [TestCase(1, 301)]
    public void It_rejects_unbounded_or_invalid_watch_settings(int passes, int seconds)
    {
        Action act = () => _status.WatchAsync([Selection()], passes, TimeSpan.FromSeconds(seconds));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
