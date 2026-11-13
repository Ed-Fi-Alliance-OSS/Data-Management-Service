// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using FakeItEasy;
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
    public async Task It_retains_the_Kafka_client_until_a_cancelled_native_call_exits(bool drainExpires)
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
        var request = CdcDeploymentRequestTestData.Request();
        var client = A.Fake<IAdminClient>();
        var entered = Signal();
        var returned = Signal();
        using var release = new ManualResetEventSlim();
        using var caller = new CancellationTokenSource();
        bool disposed = false;
        bool nativeExited = false;
        bool usedDisposedClient = false;
        A.CallTo(() => client.GetMetadata(request.Binding.TopicName, request.Timing.CallTimeout))
            .ReturnsLazily(() =>
            {
                entered.SetResult();
                release.Wait();
                usedDisposedClient = disposed;
                nativeExited = true;
                return new Metadata([], [], -1, "");
            });
        A.CallTo(() => client.Dispose()).Invokes(() => disposed = true);
        using var adapter = new CdcKafkaAdminAdapter(
            client,
            A.Fake<ICdcKafkaAuthorizationInspection>(),
            ownsClient: true
        );
        var operation = owner.WaitAsync(
            async token =>
            {
                try
                {
                    await adapter.InspectTopicAsync(request, request.Binding.TopicName, token);
                    return false;
                }
                finally
                {
                    returned.SetResult();
                }
            },
            ObservationBudget,
            TimeSpan.FromSeconds(1),
            caller.Token
        );
        Task cleanup = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await caller.CancelAsync();
            // Adapter cancellation stays prompt even though the synchronous native call cannot cancel.
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await clock.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            nativeExited.Should().BeFalse();
            operation.IsCompleted.Should().BeFalse();
            Func<Task> finish = () => operation.WaitAsync(TimeSpan.FromSeconds(30));
            if (drainExpires)
            {
                clock.Advance(DrainBudget);
                await finish.Should().ThrowAsync<CdcProbeDrainException>();
                Func<Task> dispose = () => owner.DisposeResourcesAsync(DisposeClientAsync);
                cleanup = (await dispose.Should().ThrowAsync<CdcProbeDrainException>()).Which.Completion;
                cleanup.IsCompleted.Should().BeFalse();
                disposed.Should().BeFalse();
            }
            release.Set();
            if (drainExpires)
            {
                await cleanup.WaitAsync(TimeSpan.FromSeconds(30));
                owner.LateOutcomes.Should().Equal("Cancelled");
            }
            else
            {
                await finish.Should().ThrowAsync<OperationCanceledException>();
                await owner.DisposeResourcesAsync(DisposeClientAsync);
            }
            nativeExited.Should().BeTrue();
            usedDisposedClient.Should().BeFalse();
            disposed.Should().BeTrue();
            owner.DrainFailed.Should().Be(drainExpires);
            evidence
                .Should()
                .Equal(
                    new CdcProbeFailureEvidence(
                        "CallerCancellation",
                        drainExpires ? "Timeout" : "None",
                        drainExpires ? "Pending" : "Cancelled"
                    )
                );
        }
        finally
        {
            release.Set();
            await caller.CancelAsync();
            await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await cleanup;
        }

        Task DisposeClientAsync()
        {
            adapter.Dispose();
            return Task.CompletedTask;
        }
    }

    [TestCase("send", false, false)]
    [TestCase("send", true, false)]
    [TestCase("stream", false, false)]
    [TestCase("stream", true, false)]
    [TestCase("read", false, false)]
    [TestCase("read", true, false)]
    [TestCase("send", false, true)]
    [TestCase("send", true, true)]
    public async Task It_drains_real_adapter_transport_work_after_the_adapter_returns(
        string stage,
        bool drainExpires,
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
        using var transport = new DeferredTransport(stage);
        using var client = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
        using var caller = new CancellationTokenSource();
        var adapter = new CdcConnectRestAdapter(client, clock);
        var request = CdcDeploymentRequestTestData.Request();
        var returned = Signal();
        var operation = owner.WaitAsync(
            async token =>
            {
                try
                {
                    var result = await adapter.ReadStatusAsync(request, token);
                    result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.Timeout);
                    // Even an outer probe reporting success must retain the timed-out transport.
                    return true;
                }
                finally
                {
                    returned.SetResult();
                }
            },
            ObservationBudget,
            TimeSpan.FromSeconds(1),
            caller.Token
        );
        Task cleanup = Task.CompletedTask;
        try
        {
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (callerCancels)
            {
                await caller.CancelAsync();
            }
            else
            {
                clock.Advance(request.Timing.CallTimeout);
            }
            await transport.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            operation.IsCompleted.Should().BeFalse();
            if (!callerCancels)
            {
                clock.Advance(ObservationBudget - request.Timing.CallTimeout);
            }
            await clock.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            evidence.Should().BeEmpty();
            Func<Task> finish = () => operation.WaitAsync(TimeSpan.FromSeconds(30));
            if (drainExpires)
            {
                clock.Advance(DrainBudget);
                await finish.Should().ThrowAsync<CdcProbeDrainException>();
                owner.DrainFailed.Should().BeTrue();
                Func<Task> dispose = () => owner.DisposeResourcesAsync(DisposeClientAsync);
                cleanup = (await dispose.Should().ThrowAsync<CdcProbeDrainException>()).Which.Completion;
                cleanup.IsCompleted.Should().BeFalse();
                transport.ResourcesDisposed.Should().BeFalse();
            }
            transport.Exit.SetResult();
            if (drainExpires)
            {
                await cleanup.WaitAsync(TimeSpan.FromSeconds(30));
                owner.LateOutcomes.Should().Equal(callerCancels ? "Cancelled" : "Completed");
            }
            else
            {
                if (callerCancels)
                {
                    await finish.Should().ThrowAsync<OperationCanceledException>();
                }
                else
                {
                    await finish.Should().ThrowAsync<TimeoutException>();
                }
                owner.DrainFailed.Should().BeFalse();
                await owner.DisposeResourcesAsync(DisposeClientAsync);
            }
            transport.ResourcesDisposed.Should().BeTrue();
            transport.Mutations.Should().Be(1);
            transport.MutatedAfterDisposal.Should().BeFalse();
            string completion = callerCancels ? "Cancelled" : "Completed";
            evidence
                .Should()
                .Equal(
                    new CdcProbeFailureEvidence(
                        callerCancels ? "CallerCancellation" : "ObservationTimeout",
                        drainExpires ? "Timeout" : "None",
                        drainExpires ? "Pending" : completion
                    )
                );
        }
        finally
        {
            transport.Exit.TrySetResult();
            await caller.CancelAsync();
            await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await cleanup;
        }

        Task DisposeClientAsync()
        {
            transport.ResourcesDisposed = true;
            client.Dispose();
            return Task.CompletedTask;
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_includes_an_already_running_parent_cancellation_callback_in_the_drain(
        bool callerCancels
    )
    {
        // FakeTimeProvider serializes timer callbacks. Give the drain its own scheduler so an
        // intentionally blocked observation callback cannot prevent the drain timer from firing.
        var drainClock = new FakeTimeProvider();
        var clock = new DrainClock(drainClock);
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
        using var caller = new CancellationTokenSource();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = Signal();
        var probeExit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        bool disposed = false;
        bool callbackExited = false;
        bool callbackUsedDisposedResources = false;
        var operation = owner.WaitAsync(
            token =>
            {
                registration = token.Register(() =>
                {
                    callbackEntered.SetResult();
                    releaseCallback.Wait();
                    callbackUsedDisposedResources = disposed;
                    callbackExited = true;
                });
                return probeExit.Task;
            },
            ObservationBudget,
            TimeSpan.FromSeconds(1),
            caller.Token
        );
        Task cancellation = Task.Run(() =>
        {
            if (callerCancels)
            {
                caller.Cancel();
            }
            else
            {
                clock.Advance(ObservationBudget);
            }
        });
        Task cleanup = Task.CompletedTask;
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            probeExit.SetResult(true);
            await clock.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            drainClock.Advance(DrainBudget);
            Func<Task> finish = () => operation.WaitAsync(TimeSpan.FromSeconds(30));
            await finish.Should().ThrowAsync<CdcProbeDrainException>();
            callbackExited.Should().BeFalse();
            owner.DrainFailed.Should().BeTrue();
            evidence
                .Should()
                .Equal(
                    new CdcProbeFailureEvidence(
                        callerCancels ? "CallerCancellation" : "ObservationTimeout",
                        "Timeout",
                        "Pending"
                    )
                );
            Func<Task> dispose = () =>
                owner.DisposeResourcesAsync(() =>
                {
                    disposed = true;
                    return Task.CompletedTask;
                });
            cleanup = (await dispose.Should().ThrowAsync<CdcProbeDrainException>()).Which.Completion;
            cleanup.IsCompleted.Should().BeFalse();
            disposed.Should().BeFalse();
            releaseCallback.Set();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(30));
            callbackExited.Should().BeTrue();
            callbackUsedDisposedResources.Should().BeFalse();
            disposed.Should().BeTrue();
        }
        finally
        {
            probeExit.TrySetResult(true);
            releaseCallback.Set();
            await cancellation;
            await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await cleanup;
            await registration.DisposeAsync();
        }
    }

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

    private sealed class DeferredTransport(string stage) : HttpMessageHandler
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Cancelled { get; } = Signal();
        internal TaskCompletionSource Exit { get; } = Signal();
        internal bool ResourcesDisposed { get; set; }
        internal bool MutatedAfterDisposal { get; private set; }
        internal int Mutations { get; private set; }

        private async Task WaitForExitAsync(CancellationToken token)
        {
            Entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                Cancelled.SetResult();
                await Exit.Task;
                MutatedAfterDisposal = ResourcesDisposed;
                Mutations++;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (stage == "send")
            {
                await WaitForExitAsync(cancellationToken);
            }
            return new(HttpStatusCode.OK)
            {
                Content =
                    stage == "stream"
                        ? new DeferredContent(this)
                        : new StreamContent(new DeferredStream(this)),
            };
        }

        private sealed class DeferredContent(DeferredTransport owner) : HttpContent
        {
            protected override async Task<Stream> CreateContentReadStreamAsync(
                CancellationToken cancellationToken
            )
            {
                await owner.WaitForExitAsync(cancellationToken);
                return Stream.Null;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
                throw new AssertionException("The adapter should request a stream directly.");

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }

        private sealed class DeferredStream(DeferredTransport owner) : MemoryStream
        {
            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default
            )
            {
                await owner.WaitForExitAsync(cancellationToken);
                return 0;
            }
        }
    }

    private sealed class DrainClock(FakeTimeProvider drainClock = null!) : FakeTimeProvider
    {
        internal TaskCompletionSource DrainStarted { get; } = Signal();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            var timer =
                dueTime == DrainBudget && drainClock is not null
                    ? drainClock.CreateTimer(callback, state, dueTime, period)
                    : base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == DrainBudget)
            {
                DrainStarted.TrySetResult();
            }
            return timer;
        }
    }
}
