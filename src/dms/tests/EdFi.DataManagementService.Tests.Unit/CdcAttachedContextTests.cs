// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Tests.E2E.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_CdcRuntimeOwnership
{
    private CdcRuntimeOwner _owner = null!;
    private readonly List<RecordingRuntime> _runtimes = [];
    private readonly List<string> _events = [];

    [SetUp]
    public async Task Setup()
    {
        _runtimes.Clear();
        _events.Clear();
        _owner = new(token =>
        {
            token.ThrowIfCancellationRequested();
            var runtime = new RecordingRuntime(_events, _runtimes.Count);
            _runtimes.Add(runtime);
            _events.Add($"create-{runtime.Id}");
            return Task.FromResult<(ICdcProjectionRuntime, CdcProjectionGate)>(
                (runtime, new(DocumentCacheTargetKey.Create("", 7), TimeSpan.FromSeconds(5)))
            );
        });
        await _owner.InPhaseAsync(
            async token =>
            {
                await _owner.OpenAsync(token);
                return true;
            },
            CancellationToken.None
        );
    }

    [TearDown]
    public async Task Teardown() => await _owner.DisposeAsync();

    [Test]
    public void It_does_not_start_the_executor_when_attached() => _runtimes[0].Starts.Should().Be(0);

    [Test]
    public async Task It_disposes_before_creating_and_retargets_every_subsequent_operation()
    {
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runtimes[0].OnDispose = async () =>
        {
            stopping.SetResult();
            await stopped.Task;
        };
        var replacement = _owner.InPhaseAsync(
            async token =>
            {
                await _owner.StopAsync();
                await _owner.OpenAsync(token);
                return true;
            },
            CancellationToken.None
        );
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var controller = _owner.InPhaseAsync(
            async token =>
            {
                await _owner.Runtime.StartProcessingAsync(token);
                return _owner.Runtime;
            },
            CancellationToken.None
        );
        try
        {
            _events.Should().Equal("create-0");
            controller.IsCompleted.Should().BeFalse();
        }
        finally
        {
            stopped.SetResult();
        }
        await replacement;
        (await controller).Should().BeSameAs(_runtimes[1]);
        _events.Should().Equal("create-0", "dispose-0", "create-1", "start-1");
        _runtimes[0].Starts.Should().Be(0);
    }

    [Test]
    public async Task It_exposes_an_outage_without_reusing_a_disposed_runtime()
    {
        await _owner.InPhaseAsync(
            async token =>
            {
                await _owner.StopAsync();
                Action read = () => _ = _owner.Runtime;
                read.Should().Throw<InvalidOperationException>().WithMessage("CDC_API_RUNTIME_STOPPED");
                await _owner.OpenAsync(token);
                return true;
            },
            CancellationToken.None
        );
        _owner.Runtime.Should().BeSameAs(_runtimes[1]);
    }

    [Test]
    public async Task It_cancels_waiting_controller_operations_without_entering_the_phase()
    {
        using var cancelled = new CancellationTokenSource();
        await _owner.InPhaseAsync(
            async _ =>
            {
                var waiting = _owner.InPhaseAsync<bool>(
                    _ => throw new AssertionException("Entered blocked phase"),
                    cancelled.Token
                );
                await cancelled.CancelAsync();
                Func<Task> act = async () => await waiting;
                await act.Should().ThrowAsync<OperationCanceledException>();
                return true;
            },
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_disposes_the_gate_and_forbids_replacement_after_stop_failure()
    {
        var gate = _owner.Gate;
        _runtimes[0].OnDispose = () => throw new InvalidOperationException("stop failed");
        await _owner.InPhaseAsync(
            async token =>
            {
                Func<Task> stop = _owner.StopAsync;
                await stop.Should().ThrowAsync<InvalidOperationException>().WithMessage("stop failed");
                Action pause = () => gate.Pause();
                pause.Should().Throw<ObjectDisposedException>();
                Func<Task> open = () => _owner.OpenAsync(token);
                await open.Should().ThrowAsync<InvalidOperationException>().WithMessage("*STOP_FAILED");
                return true;
            },
            CancellationToken.None
        );
        _runtimes.Should().HaveCount(1);
    }

    [Test]
    public async Task It_disposes_once_and_rejects_later_operations()
    {
        await _owner.DisposeAsync();
        await _owner.DisposeAsync();
        _events.Should().Equal("create-0", "dispose-0");
        Func<Task> act = () => _owner.InPhaseAsync(_ => Task.FromResult(true), CancellationToken.None);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task It_waits_for_an_active_phase_before_disposal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var phase = _owner.InPhaseAsync(
            async _ =>
            {
                entered.SetResult();
                await release.Task;
                return true;
            },
            CancellationToken.None
        );
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = _owner.DisposeAsync().AsTask();
        try
        {
            disposal.IsCompleted.Should().BeFalse();
            _events.Should().Equal("create-0");
        }
        finally
        {
            release.SetResult();
        }
        await phase;
        await disposal;
        _events.Should().Equal("create-0", "dispose-0");
    }

    [Test]
    public async Task It_keeps_the_old_runtime_unavailable_when_replacement_creation_fails()
    {
        await _owner.InPhaseAsync(
            async ignoredToken =>
            {
                await _owner.StopAsync();
                using var cancelled = new CancellationTokenSource();
                await cancelled.CancelAsync();
                Func<Task> open = () => _owner.OpenAsync(cancelled.Token);
                await open.Should().ThrowAsync<OperationCanceledException>();
                Action read = () => _ = _owner.Runtime;
                read.Should().Throw<InvalidOperationException>().WithMessage("CDC_API_RUNTIME_STOPPED");
                return true;
            },
            CancellationToken.None
        );
        _events.Should().Equal("create-0", "dispose-0");
    }

    private sealed class RecordingRuntime(List<string> events, int id) : ICdcProjectionRuntime
    {
        public int Id => id;
        public int Starts { get; private set; }
        public Func<Task> OnDispose { get; set; } = () => Task.CompletedTask;

        public Task StartProcessingAsync(CancellationToken cancellationToken)
        {
            Starts++;
            events.Add($"start-{id}");
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await OnDispose();
            events.Add($"dispose-{id}");
        }

        public Task<DocumentCacheAdministrativeCommandResult> ActivateAsync(
            DocumentCacheGuardedNewEmptyActivationRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CdcInitialDatabaseObservation> ObserveInitialDatabaseAsync(
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CdcInitialDatabaseObservation> ObserveEstablishedDatabaseAsync(
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CdcProviderBarrierCaptureResult> CaptureBarrierAsync(
            CdcDeploymentRequest request,
            ICdcProviderSourcePositionAdapter adapter,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<DocumentCacheStatusResponse> ObserveAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

[TestFixture]
public class Given_CdcAttachmentCleanup
{
    private CdcAttachmentResources _resources = null!;
    private List<int> _disposed = null!;

    [SetUp]
    public void Setup()
    {
        _disposed = [];
        _resources = new();
        _resources.Add(new Cleanup(() => _disposed.Add(1)));
        _resources.Add(
            new Cleanup(() =>
            {
                _disposed.Add(2);
                throw new InvalidOperationException("private connection");
            })
        );
        _resources.Add(new Cleanup(() => _disposed.Add(3)));
    }

    [Test]
    public async Task It_attempts_every_disposal_in_reverse_order_and_sanitizes_failure()
    {
        Func<Task> dispose = async () => await _resources.DisposeAsync();
        var failure = await dispose
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("CDC_API_ATTACHMENT_DISPOSAL");
        failure.Which.InnerException.Should().BeNull();
        _disposed.Should().Equal(3, 2, 1);
        await _resources.DisposeAsync();
        _disposed.Should().Equal(3, 2, 1);
    }

    private sealed class Cleanup(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

[TestFixture]
[NonParallelizable]
public class Given_CdcAttachedContextWithoutHandoff
{
    private string _original = null!;
    private Exception _failure = null!;

    [SetUp]
    public async Task Setup()
    {
        _original = Environment.GetEnvironmentVariable("CDC_API_E2E_HANDOFF_PATH")!;
        Environment.SetEnvironmentVariable(
            "CDC_API_E2E_HANDOFF_PATH",
            "/missing/private-attachment-credentials.json"
        );
        try
        {
            await using CdcAttachedContext context = new();
            await context.InitializeAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _failure = exception;
        }
    }

    [TearDown]
    public void Teardown() => Environment.SetEnvironmentVariable("CDC_API_E2E_HANDOFF_PATH", _original);

    [Test]
    public void It_fails_explicit_execution_with_only_a_sanitized_boundary_code()
    {
        var failure = _failure.Should().BeOfType<CdcAttachmentException>().Subject;
        failure.Boundary.Should().Be(CdcAttachmentBoundary.Handoff);
        failure.Failure.Should().Be(CdcScenarioFailure.Error);
        _failure.Message.Should().Be("CDC_API_ATTACHMENT_FAILED");
        _failure.ToString().Should().NotContain("private-attachment-credentials");
        _failure.InnerException.Should().BeNull();
    }
}
