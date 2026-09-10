// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(Ddl.CdcProvider.Postgresql)]
[TestFixture(Ddl.CdcProvider.SqlServer)]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix owner-only permissions.")]
internal class Given_CdcManagedLifecycle(Ddl.CdcProvider provider) : CdcReadinessTestBase(provider)
{
    private CdcManagedLifecycle _managed = null!;
    private ICdcBindingLifecycleService _bindings = null!;
    private bool _stopped;
    private bool _failed;
    private int _stops;
    private int _resumes;
    private int _restarts;

    [SetUp]
    public void SetupLifecycle()
    {
        _bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        _stopped = _failed = false;
        _stops = _resumes = _restarts = 0;
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("stop");
                _stops++;
                _stopped = true;
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("resume");
                _resumes++;
                _stopped = _failed = false;
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.RestartAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("restart");
                _restarts++;
                _stopped = _failed = false;
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("status");
                var status = Status();
                if (_stopped)
                {
                    return Observed(
                        new CdcConnectStatus(
                            status.Runtime with
                            {
                                ConnectorState = CdcConnectorRuntimeState.Stopped,
                                SoleTaskState = CdcConnectorRuntimeState.Stopped,
                                TaskCount = 0,
                                RunningTaskCount = 0,
                            },
                            status.WorkerId,
                            []
                        )
                    );
                }
                return Observed(
                    !_failed
                        ? status
                        : new CdcConnectStatus(
                            status.Runtime with
                            {
                                SoleTaskState = CdcConnectorRuntimeState.Failed,
                                LastErrorCategory = "connect-runtime-failed",
                                RunningTaskCount = 0,
                            },
                            status.WorkerId,
                            [new(0, CdcConnectorRuntimeState.Failed, status.WorkerId)]
                        )
                );
            });
        _trace.Clear();
        _calls.Clear();
        Fake.ClearRecordedCalls(_connect);
        Fake.ClearRecordedCalls(_kafka);
        Fake.ClearRecordedCalls(_runtime);
        ResetManaged();
    }

    private void ReplaceWorker(string identity) =>
        _workerEvidence = new(
            identity,
            _workerEvidence.MetricsEndpoint,
            _workerEvidence.EffectiveConfiguration,
            _workerEvidence.ImageDigest,
            _workerEvidence.HeapBytes,
            _workerEvidence.ConnectWorkerId
        );

    private void ResetManaged()
    {
        CdcEstablishedValidation validation = new(
            _store,
            _bindings,
            _provider,
            _templates,
            _kafka,
            _connect,
            _worker,
            _metrics,
            _positions,
            TimeProvider.System
        );
        CdcControllerStatus status = new(_store, _bindings, _connect, _ => validation, TimeProvider.System);
        _managed = new(_store, _bindings, _connect, _worker, status, TimeProvider.System);
    }

    private Task<CdcManagedLifecycleResult> Execute(
        CdcManagedLifecycleOperation operation,
        CancellationToken token = default
    ) => _managed.ExecuteAsync(new(_request, _runtime, 1000), operation, token);

    [TearDown]
    public void It_retains_artifacts_and_never_uses_initial_activation_or_projection_ownership()
    {
        Fake.GetCalls(_connect)
            .Should()
            .NotContain(c =>
                !c.Method.Name.StartsWith("Read", StringComparison.Ordinal)
                && c.Method.Name != "StopAsync"
                && c.Method.Name != "ResumeAsync"
                && c.Method.Name != "RestartAsync"
            );
        Fake.GetCalls(_kafka)
            .Should()
            .NotContain(c => !c.Method.Name.StartsWith("Inspect", StringComparison.Ordinal));
        A.CallTo(() => _runtime.StartProcessingAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() =>
                _runtime.ActivateAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        _barriers.Should().Be(0);
        _disposals.Should().Be(0);
        _calls.Should().NotContain(r => r.Mode != Ddl.CdcProviderSetupMode.ValidateOnly);
    }

    [TestCase(3)]
    [TestCase(4)]
    public async Task It_preserves_terminal_results_after_restart_including_final_validation(int terminalPass)
    {
        ShortTiming(1000);
        using var deadline = new CancellationTokenSource();
        int passes = 0;
        A.CallTo(() => _runtime.InitializeAsync(A<CancellationToken>._))
            .Invokes(() =>
            {
                if (++passes == terminalPass)
                {
                    _identity = new('b', 64);
                }
            });
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (CdcDeploymentRequest _, CancellationToken ct) =>
                {
                    _stops++;
                    _stopped = true;
                    await deadline.CancelAsync();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return Observed(new CdcTransportAcknowledgement());
                }
            );
        var result = await _managed.ExecuteAsync(
            new(_request, _runtime, 1000),
            CdcManagedLifecycleOperation.Restart,
            default,
            deadline.Token
        );
        result.Succeeded.Should().BeFalse();
        result.Ready.Should().BeFalse();
        result.Observation.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result.Observation.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Persisted);
        result.Observation.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
        result
            .Diagnostics.Should()
            .Contain(d =>
                d.Component == CdcDeploymentComponent.Connect && d.Failure == CdcDeploymentFailure.Timeout
            );
        _stops.Should().Be(1);
        _restarts.Should().Be(1);
        _resumes.Should().Be(0);
        passes.Should().Be(terminalPass);
    }

    [TestCase("operation-wait", false)]
    [TestCase("latch", false)]
    [TestCase("stop", false)]
    [TestCase("read-back", false)]
    [TestCase("latch", true)]
    [TestCase("stop", true)]
    [TestCase("read-back", true)]
    public async Task It_preserves_terminal_containment_budgets_and_honors_only_caller_cancellation(
        string boundary,
        bool cancelCaller
    )
    {
        ShortTiming(100);
        _identity = new('b', 64);
        CancellationToken observationToken = default;
        if (boundary == "operation-wait")
        {
            // Equal call/wait budgets guarantee the operation expires during the latch, after
            // terminal evidence is captured, without depending on a narrow scheduling window.
            _request = new(
                _request.Binding,
                _request.DmsSettings,
                _request.ProviderSetup,
                _request.ConnectEndpoint,
                _request.WorkerMetricsEndpoint,
                _request.ConnectorPolicy,
                _request.WorkerPolicy,
                _request.ProviderConnectionProperties,
                _request.KafkaClientSecurityProperties,
                new(_request.Timing.WaitTimeout, _request.Timing.WaitTimeout, _request.Timing.PollInterval)
            );
            A.CallTo(() => _runtime.InitializeAsync(A<CancellationToken>._))
                .ReturnsLazily(
                    (CancellationToken ct) =>
                    {
                        observationToken = ct;
                        return Task.CompletedTask;
                    }
                );
        }
        using var caller = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        var real = _services.GetRequiredService<ICdcBindingLifecycleService>();
        var bindings = A.Fake<ICdcBindingLifecycleService>();
        A.CallTo(() => bindings.ExactMatchBindingAsync(A<CdcBinding>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcBinding binding, CancellationToken ct) => real.ExactMatchBindingAsync(binding, ct)
            );
        A.CallTo(() => bindings.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (CdcIncident incident, CancellationToken ct) =>
                {
                    if (boundary is "latch" or "operation-wait")
                    {
                        if (boundary == "latch")
                        {
                            Expire();
                        }
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    return await real.LatchSourceHistoryLossAsync(incident, ct);
                }
            );
        int stops = 0;
        int readBacks = 0;
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (CdcDeploymentRequest _, CancellationToken ct) =>
                {
                    stops++;
                    _stopped = true;
                    if (boundary == "operation-wait")
                    {
                        observationToken.IsCancellationRequested.Should().BeTrue();
                        ct.IsCancellationRequested.Should().BeFalse();
                    }
                    if (boundary == "stop")
                    {
                        Expire();
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    return Observed(new CdcTransportAcknowledgement());
                }
            );
        _onCall = name =>
        {
            if (name == "status" && _stopped)
            {
                readBacks++;
                if (boundary == "read-back")
                {
                    Expire();
                }
            }
        };
        _bindings = bindings;
        ResetManaged();
        async Task Run()
        {
            var result = await _managed.ExecuteAsync(
                new(_request, _runtime, 1000),
                CdcManagedLifecycleOperation.Restart,
                caller.Token,
                deadline.Token
            );
            result.Succeeded.Should().BeFalse();
            result.Ready.Should().BeFalse();
            var observation = result.Observation;
            observation.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
            observation.Status.Readiness.Should().Be(CdcReadiness.NotReady);
            observation
                .IncidentPersistence.Should()
                .Be(
                    boundary is "latch" or "operation-wait"
                        ? CdcIncidentPersistenceState.Failed
                        : CdcIncidentPersistenceState.Persisted
                );
            observation.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
            stops.Should().Be(1);
            readBacks.Should().BeGreaterThan(0);
            if (boundary != "read-back")
            {
                result
                    .Diagnostics.Should()
                    .Contain(d =>
                        d.Failure == CdcDeploymentFailure.Timeout
                        && d.Component
                            == (
                                (boundary == "latch" || boundary == "operation-wait")
                                    ? CdcDeploymentComponent.WorkflowState
                                    : CdcDeploymentComponent.Connect
                            )
                    );
            }
            JsonSerializer.Serialize(result).Should().NotContain("private");
            deadline.IsCancellationRequested.Should().Be(boundary != "operation-wait");
        }
        if (cancelCaller)
        {
            await FluentActions.Awaiting(Run).Should().ThrowAsync<OperationCanceledException>();
            stops.Should().Be(boundary == "latch" ? 0 : 1);
        }
        else
        {
            await Run();
        }
        _resumes.Should().Be(0);
        _restarts.Should().Be(0);
        void Expire()
        {
            if (cancelCaller)
            {
                caller.Cancel();
            }
            else
            {
                deadline.Cancel();
            }
        }
    }

    [Test]
    public async Task It_verifies_and_journals_stop_while_retaining_all_other_state()
    {
        var retained = Directory
            .GetFiles(_root, "*.json", SearchOption.AllDirectories)
            .Where(p => !p.Contains("/workflows/", StringComparison.Ordinal))
            .ToDictionary(p => p, File.ReadAllBytes);
        var result = await Execute(CdcManagedLifecycleOperation.Stop);
        result.Succeeded.Should().BeTrue();
        result.TargetShutdownVerified.Should().BeTrue();
        result.Ready.Should().BeFalse();
        result.Boundary.Should().Be(CdcManagedLifecycleBoundary.VerifiedManagedStop);
        ReadJournal()
            .Operations.Last()
            .Completions.Single()
            .Evidence.Should()
            .BeOfType<CdcWorkflowCompletion.Shutdown>();
        foreach (var pair in retained)
        {
            (await File.ReadAllBytesAsync(pair.Key)).Should().Equal(pair.Value);
        }
        _stops.Should().Be(1);
        _trace.Count(s => s == "status").Should().BeGreaterThan(2);
        _trace.Should().NotContain("offset").And.NotContain("metrics");
    }

    [TestCase(CdcManagedLifecycleOperation.Start)]
    [TestCase(CdcManagedLifecycleOperation.Resume)]
    [TestCase(CdcManagedLifecycleOperation.Restart)]
    public async Task It_reads_retained_stopped_offsets_before_resume_and_requires_new_running_lag(
        CdcManagedLifecycleOperation operation
    )
    {
        (await Execute(CdcManagedLifecycleOperation.Stop)).Succeeded.Should().BeTrue();
        ReplaceWorker("restarted-worker");
        ResetManaged();
        _trace.Clear();
        var result = await Execute(operation);
        result.Succeeded.Should().BeTrue();
        result.Ready.Should().BeTrue();
        result.TargetShutdownVerified.Should().BeFalse();
        result.Boundary.Should().Be(CdcManagedLifecycleBoundary.VerifiedManagedStop);
        _resumes.Should().Be(1);
        _restarts.Should().Be(0);
        _trace.IndexOf("offset").Should().BeLessThan(_trace.IndexOf("resume"));
        _trace.IndexOf("metrics").Should().BeGreaterThan(_trace.IndexOf("resume"));
        ReadJournal().Operations.Last().Effect.Should().Be(CdcWorkflowEffect.ResumeConnector);
        ReadJournal().Operations.Last().Completions.Should().HaveCount(1);
    }

    [TestCase(CdcManagedLifecycleOperation.Start, "")]
    [TestCase(CdcManagedLifecycleOperation.Start, "private-other-broker:9092")]
    [TestCase(CdcManagedLifecycleOperation.Resume, "")]
    [TestCase(CdcManagedLifecycleOperation.Resume, "private-other-broker:9092")]
    [TestCase(CdcManagedLifecycleOperation.Restart, "")]
    [TestCase(CdcManagedLifecycleOperation.Restart, "private-other-broker:9092")]
    public async Task It_rejects_worker_broker_drift_before_managed_resume(
        CdcManagedLifecycleOperation operation,
        string endpoint
    )
    {
        (await Execute(CdcManagedLifecycleOperation.Stop)).Succeeded.Should().BeTrue();
        ChangeWorkerBootstrap(endpoint);
        var result = await Execute(operation);
        result.Succeeded.Should().BeFalse();
        result.Ready.Should().BeFalse();
        _resumes.Should().Be(0);
        _restarts.Should().Be(0);
        JsonSerializer.Serialize(result).Should().NotContain("private-other-broker");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_worker_broker_drift_before_restarting_a_running_or_failed_connector(
        bool failed
    )
    {
        _failed = failed;
        ChangeWorkerBootstrap("private-other-broker:9092");
        var result = await Execute(CdcManagedLifecycleOperation.Restart);
        result.Succeeded.Should().BeFalse();
        result.Ready.Should().BeFalse();
        _resumes.Should().Be(0);
        _restarts.Should().Be(0);
        JsonSerializer.Serialize(result).Should().NotContain("private-other-broker");
    }

    [Test]
    public async Task It_restarts_a_recoverable_failed_task_without_prestart_lag()
    {
        _failed = true;
        var result = await Execute(CdcManagedLifecycleOperation.Restart);
        result.Ready.Should().BeTrue();
        result.Boundary.Should().Be(CdcManagedLifecycleBoundary.NativeRecovery);
        _restarts.Should().Be(1);
        _resumes.Should().Be(0);
        _trace.IndexOf("metrics").Should().BeGreaterThan(_trace.IndexOf("restart"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_does_not_treat_acknowledgement_or_lost_stop_reply_as_shutdown(bool lostReply)
    {
        ShortTiming(30);
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
                lostReply
                    ? throw new TimeoutException("private-password")
                    : Observed(new CdcTransportAcknowledgement())
            );
        var result = await Execute(CdcManagedLifecycleOperation.Stop);
        result.Succeeded.Should().BeFalse();
        result.TargetShutdownVerified.Should().BeFalse();
        ReadJournal().Operations.Last().Completions.Should().BeEmpty();
        result.Diagnostics.Should().Contain(d => d.Failure == CdcDeploymentFailure.Timeout);
        JsonSerializer.Serialize(result).Should().NotContain("private-password");
    }

    [Test]
    public async Task It_reconciles_a_lost_stop_response_from_independent_live_readback()
    {
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Invokes(() => _stopped = true)
            .Throws(new IOException("private-password"));
        var result = await Execute(CdcManagedLifecycleOperation.Stop);
        result.TargetShutdownVerified.Should().BeTrue();
        ReadJournal()
            .Operations.Last()
            .Completions.Single()
            .Evidence.Should()
            .BeOfType<CdcWorkflowCompletion.Shutdown>();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_routes_missing_or_contradictory_retained_stop_to_native_recovery(bool receipt)
    {
        if (receipt)
        {
            (await Execute(CdcManagedLifecycleOperation.Stop)).Succeeded.Should().BeTrue();
        }
        _stopped = !receipt;
        var result = await Execute(CdcManagedLifecycleOperation.Start);
        result.Succeeded.Should().BeFalse();
        result.Boundary.Should().Be(CdcManagedLifecycleBoundary.NativeRecovery);
        _resumes.Should().Be(0);
        _restarts.Should().Be(0);
    }

    [Test]
    public async Task It_rejects_legacy_generic_stop_completion_as_verified_shutdown()
    {
        await CompleteAsync(CdcWorkflowEffect.StopConnector);
        _stopped = true;
        (await Execute(CdcManagedLifecycleOperation.Start))
            .Boundary.Should()
            .Be(CdcManagedLifecycleBoundary.NativeRecovery);
        _resumes.Should().Be(0);
    }

    [TestCase("provider")]
    [TestCase("offset")]
    [TestCase("schema-history")]
    [TestCase("worker")]
    [TestCase("config")]
    [TestCase("topic")]
    public async Task It_rejects_unknown_prestart_evidence_without_mutation(string stage)
    {
        if (stage == "schema-history" && Provider == Ddl.CdcProvider.Postgresql)
        {
            return;
        }
        (await Execute(CdcManagedLifecycleOperation.Stop)).Succeeded.Should().BeTrue();
        _onCall = name =>
        {
            if (name == stage)
            {
                throw new IOException("private-password");
            }
        };
        var result = await Execute(CdcManagedLifecycleOperation.Start);
        result.Succeeded.Should().BeFalse();
        _resumes.Should().Be(0);
        _restarts.Should().Be(0);
        JsonSerializer.Serialize(result).Should().NotContain("private-password");
    }

    [TestCase(CdcConnectOffsetState.Missing)]
    [TestCase(CdcConnectOffsetState.Null)]
    [TestCase(CdcConnectOffsetState.Malformed)]
    [TestCase(CdcConnectOffsetState.Snapshot)]
    public async Task It_latches_and_contains_offset_loss_before_controller_restart(
        CdcConnectOffsetState state
    )
    {
        _offsetState = state;
        (await Execute(CdcManagedLifecycleOperation.Restart)).Succeeded.Should().BeFalse();
        _restarts.Should().Be(0);
        _stops.Should().Be(1);
        (await _bindings.ExactMatchBindingAsync(_request.Binding))
            .State!.State.Should()
            .Be(CdcBindingState.IncidentLatched);
        _offsetState = CdcConnectOffsetState.Streaming;
        (await Execute(CdcManagedLifecycleOperation.Resume)).Succeeded.Should().BeFalse();
        _resumes.Should().Be(0);
    }

    [TestCase("workflows")]
    [TestCase("source-history")]
    public async Task It_rejects_missing_provenance_without_reconstructing_it(string directory)
    {
        Directory.Delete(Path.Combine(_root, directory), true);
        (await Execute(CdcManagedLifecycleOperation.Restart)).Succeeded.Should().BeFalse();
        _restarts.Should().Be(0);
        _resumes.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_requires_fresh_post_mutation_lag_and_projection(bool backlog)
    {
        _backlog = backlog;
        _lag = backlog ? 1 : 1001;
        var result = await Execute(CdcManagedLifecycleOperation.Restart);
        _restarts.Should().Be(1);
        result.Ready.Should().BeFalse();
        result.Succeeded.Should().BeFalse();
        result.TargetShutdownVerified.Should().BeFalse();
    }

    [TestCase(CdcWorkflowWriteBoundary.BeforeTemporaryWrite)]
    [TestCase(CdcWorkflowWriteBoundary.AfterTemporaryFlush)]
    [TestCase(CdcWorkflowWriteBoundary.AfterAtomicReplacement)]
    public async Task It_never_returns_shutdown_permission_when_journal_completion_is_interrupted(
        CdcWorkflowWriteBoundary boundary
    )
    {
        int writes = 0;
        _onWrite = b =>
        {
            if (b == boundary && ++writes == 2)
            {
                throw new IOException("private-journal-failure");
            }
        };
        var result = await Execute(CdcManagedLifecycleOperation.Stop);
        result.Succeeded.Should().BeFalse();
        result.TargetShutdownVerified.Should().BeFalse();
        _onWrite = _ => { };
        if (boundary != CdcWorkflowWriteBoundary.AfterAtomicReplacement)
        {
            ReadJournal().Operations.Last().Completions.Should().BeEmpty();
            (await Execute(CdcManagedLifecycleOperation.Start)).Succeeded.Should().BeFalse();
            _resumes.Should().Be(0);
        }
        (await Execute(CdcManagedLifecycleOperation.Stop)).TargetShutdownVerified.Should().BeTrue();
    }

    [Test]
    public async Task It_rejects_worker_recovery_between_shutdown_and_journaling()
    {
        int reads = 0;
        _onCall = name =>
        {
            if (name == "status" && ++reads == 2)
            {
                ReplaceWorker("other-worker");
            }
        };
        (await Execute(CdcManagedLifecycleOperation.Stop)).TargetShutdownVerified.Should().BeFalse();
        ReadJournal().Operations.Last().Completions.Should().BeEmpty();
    }

    [Test]
    public async Task It_propagates_cancellation_after_stop_without_shutdown_permission()
    {
        using var cancellation = new CancellationTokenSource();
        _onCall = name =>
        {
            if (name == "status")
            {
                cancellation.Cancel();
            }
        };
        Func<Task> action = () => Execute(CdcManagedLifecycleOperation.Stop, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        ReadJournal().Operations.Last().Completions.Should().BeEmpty();
    }

    [Test]
    public async Task It_serializes_lifecycle_mutation_under_the_same_root_lock()
    {
        ShortTiming(30);
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(5),
            CancellationToken.None
        );
        var result = await Execute(CdcManagedLifecycleOperation.Restart);
        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Failure == CdcDeploymentFailure.Timeout);
        _restarts.Should().Be(0);
    }

    [TestCase(CdcManagedLifecycleOperation.Start)]
    [TestCase(CdcManagedLifecycleOperation.Restart)]
    [TestCase(CdcManagedLifecycleOperation.Resume)]
    public async Task It_rejects_pending_record_size_increases_even_with_aligned_live_limits(
        CdcManagedLifecycleOperation operation
    )
    {
        (await Execute(CdcManagedLifecycleOperation.Stop)).Succeeded.Should().BeTrue();
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
        var before = JsonSerializer.Serialize(ReadJournal());
        (await Execute(operation)).Succeeded.Should().BeFalse();
        JsonSerializer.Serialize(ReadJournal()).Should().Be(before);
        _resumes.Should().Be(0);
        _restarts.Should().Be(0);
        ReadJournal().HasPendingRecordSizeIncrease.Should().BeTrue();
        (await Execute(CdcManagedLifecycleOperation.Stop)).TargetShutdownVerified.Should().BeTrue();
        ReadJournal().HasPendingRecordSizeIncrease.Should().BeTrue();
    }

    [TestCase(CdcManagedLifecycleOperation.Start, false)]
    [TestCase(CdcManagedLifecycleOperation.Start, true)]
    [TestCase(CdcManagedLifecycleOperation.Restart, false)]
    [TestCase(CdcManagedLifecycleOperation.Restart, true)]
    [TestCase(CdcManagedLifecycleOperation.Resume, false)]
    [TestCase(CdcManagedLifecycleOperation.Resume, true)]
    public async Task It_rejects_a_different_physical_source_without_rebinding(
        CdcManagedLifecycleOperation operation,
        bool populated
    )
    {
        (await Execute(CdcManagedLifecycleOperation.Stop)).Succeeded.Should().BeTrue();
        _rows = populated;
        var change = _change;
        _change = result =>
            change(result) with
            {
                ObservedSourceFingerprint = new(
                    Ddl.CdcSourceFingerprintMetadata.Version,
                    "sha256:" + new string('f', 64)
                ),
            };
        (await Execute(operation)).Succeeded.Should().BeFalse();
        _resumes.Should().Be(0);
        _restarts.Should().Be(0);
    }

    [TestCase(CdcWorkflowWriteBoundary.BeforeTemporaryWrite)]
    [TestCase(CdcWorkflowWriteBoundary.AfterTemporaryFlush)]
    [TestCase(CdcWorkflowWriteBoundary.AfterAtomicReplacement)]
    public async Task It_never_resumes_when_intent_persistence_fails(CdcWorkflowWriteBoundary boundary)
    {
        (await Execute(CdcManagedLifecycleOperation.Stop)).Succeeded.Should().BeTrue();
        _onWrite = b =>
        {
            if (b == boundary)
            {
                throw new IOException("private-failure");
            }
        };
        (await Execute(CdcManagedLifecycleOperation.Start)).Succeeded.Should().BeFalse();
        _resumes.Should().Be(0);
        _onWrite = _ => { };
        if (boundary == CdcWorkflowWriteBoundary.AfterAtomicReplacement)
        {
            (await Execute(CdcManagedLifecycleOperation.Start))
                .Boundary.Should()
                .Be(CdcManagedLifecycleBoundary.NativeRecovery);
            _resumes.Should().Be(0);
        }
    }

    [Test]
    public async Task It_rechecks_continuity_after_intent_before_resume()
    {
        (await Execute(CdcManagedLifecycleOperation.Stop)).Succeeded.Should().BeTrue();
        _onWrite = b =>
        {
            if (b == CdcWorkflowWriteBoundary.AfterAtomicReplacement)
            {
                _offsetState = CdcConnectOffsetState.Missing;
            }
        };
        (await Execute(CdcManagedLifecycleOperation.Start)).Succeeded.Should().BeFalse();
        _resumes.Should().Be(0);
        _stops.Should().Be(2);
        (await _bindings.ExactMatchBindingAsync(_request.Binding))
            .State!.State.Should()
            .Be(CdcBindingState.IncidentLatched);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_worker_replacement_during_or_after_resume(bool afterJournal)
    {
        (await Execute(CdcManagedLifecycleOperation.Stop)).Succeeded.Should().BeTrue();
        if (afterJournal)
        {
            int writes = 0;
            _onWrite = b =>
            {
                if (b == CdcWorkflowWriteBoundary.AfterAtomicReplacement && ++writes == 2)
                {
                    ReplaceWorker("recovered-worker");
                }
            };
        }
        else
        {
            _onCall = name =>
            {
                if (name == "resume")
                {
                    ReplaceWorker("recovered-worker");
                }
            };
        }
        var result = await Execute(CdcManagedLifecycleOperation.Start);
        result.Succeeded.Should().BeFalse();
        result.Ready.Should().BeFalse();
        _resumes.Should().Be(1);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_unavailable_or_stale_stopped_status(bool stale)
    {
        var response = Status();
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
                stale
                    ? Observed(
                        new CdcConnectStatus(
                            response.Runtime with
                            {
                                ConnectorState = CdcConnectorRuntimeState.Stopped,
                                SoleTaskState = CdcConnectorRuntimeState.Stopped,
                                TaskCount = 0,
                                RunningTaskCount = 0,
                            },
                            response.WorkerId,
                            []
                        )
                    )
                    : new CdcTransportResult<CdcConnectStatus>.Unavailable(
                        new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.AuthenticationFailed)
                    )
            );
        (await Execute(CdcManagedLifecycleOperation.Stop)).TargetShutdownVerified.Should().BeFalse();
        ReadJournal().Operations.Last().Completions.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_task_state_inconsistent_with_the_stopped_summary()
    {
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                var current = Status();
                return Observed(
                    new CdcConnectStatus(
                        current.Runtime with
                        {
                            ConnectorState = CdcConnectorRuntimeState.Stopped,
                            SoleTaskState = CdcConnectorRuntimeState.Stopped,
                            TaskCount = 0,
                            RunningTaskCount = 0,
                        },
                        current.WorkerId,
                        current.Tasks
                    )
                );
            });
        ShortTiming(30);
        (await Execute(CdcManagedLifecycleOperation.Stop)).TargetShutdownVerified.Should().BeFalse();
        ReadJournal().Operations.Last().Completions.Should().BeEmpty();
    }

    [Test]
    public async Task It_collects_new_telemetry_after_completion_persistence()
    {
        _onWrite = b =>
        {
            if (b == CdcWorkflowWriteBoundary.AfterAtomicReplacement && _restarts > 0)
            {
                _lag = 1001;
            }
        };
        var result = await Execute(CdcManagedLifecycleOperation.Restart);
        _restarts.Should().Be(1);
        result.Ready.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Component == CdcDeploymentComponent.Metrics);
    }

    [Test]
    public async Task It_does_not_reuse_a_shutdown_receipt_after_an_interrupted_resume()
    {
        (await Execute(CdcManagedLifecycleOperation.Stop)).Succeeded.Should().BeTrue();
        using var cancellation = new CancellationTokenSource();
        _onCall = name =>
        {
            if (name == "resume")
            {
                cancellation.Cancel();
            }
        };
        Func<Task> action = () => Execute(CdcManagedLifecycleOperation.Start, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        _onCall = _ => { };
        _stopped = true;
        (await Execute(CdcManagedLifecycleOperation.Start))
            .Boundary.Should()
            .Be(CdcManagedLifecycleBoundary.NativeRecovery);
        _resumes.Should().Be(1);
    }

    [Test]
    public async Task It_contains_retained_incidents_even_with_missing_journal()
    {
        _offsetState = CdcConnectOffsetState.Missing;
        (await Execute(CdcManagedLifecycleOperation.Restart)).Succeeded.Should().BeFalse();
        Directory.Delete(Path.Combine(_root, "workflows"), true);
        _stopped = false;
        (await Execute(CdcManagedLifecycleOperation.Start)).Succeeded.Should().BeFalse();
        _stops.Should().Be(2);
        _resumes.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_does_not_infer_a_restart_from_unchanged_running_status_after_a_lost_reply(
        bool initiallyStopped
    )
    {
        if (initiallyStopped)
        {
            (await Execute(CdcManagedLifecycleOperation.Stop)).Succeeded.Should().BeTrue();
            A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                .Invokes(() => _stopped = false)
                .Throws(new TimeoutException());
        }
        else
        {
            A.CallTo(() => _connect.RestartAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                .Throws(new TimeoutException());
        }
        var result = await Execute(CdcManagedLifecycleOperation.Restart);
        result.Succeeded.Should().Be(initiallyStopped);
        result.Ready.Should().Be(initiallyStopped);
        ReadJournal().Operations.Last().Completions.Length.Should().Be(initiallyStopped ? 1 : 0);
    }
}
