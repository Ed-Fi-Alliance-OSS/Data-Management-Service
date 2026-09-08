// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
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
internal class Given_CdcNativeRecovery(Ddl.CdcProvider provider) : CdcReadinessTestBase(provider)
{
    private CdcControllerStatus _status = null!;
    private CdcManagedLifecycle _managed = null!;
    private CdcEstablishedValidation _validation = null!;
    private bool _stopped;
    private bool _failed;
    private int _stops;
    private string _assignment = "";

    [SetUp]
    public void SetupRecovery()
    {
        _stopped = _failed = false;
        _stops = 0;
        _assignment = _workerEvidence.ConnectWorkerId;
        var bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        _validation = new(
            _store,
            bindings,
            _provider,
            _templates,
            _kafka,
            _connect,
            _worker,
            _metrics,
            _positions,
            TimeProvider.System
        );
        _status = new(_store, bindings, _connect, _ => _validation, TimeProvider.System);
        _managed = new(_store, bindings, _connect, _worker, _status, TimeProvider.System);
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("status");
                var current = Status();
                var taskState = (_stopped, _failed) switch
                {
                    (true, _) => CdcConnectorRuntimeState.Stopped,
                    (_, true) => CdcConnectorRuntimeState.Failed,
                    _ => CdcConnectorRuntimeState.Running,
                };
                return Observed(
                    new CdcConnectStatus(
                        current.Runtime with
                        {
                            ConnectorState = _stopped
                                ? CdcConnectorRuntimeState.Stopped
                                : CdcConnectorRuntimeState.Running,
                            SoleTaskState = taskState,
                            TaskCount = _stopped ? 0 : 1,
                            RunningTaskCount = _stopped || _failed ? 0 : 1,
                        },
                        _workerEvidence.ConnectWorkerId,
                        _stopped ? [] : [new(0, taskState, _assignment)]
                    )
                );
            });
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _stopped = true;
                _stops++;
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _stopped = _failed = false;
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.RestartAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _stopped = _failed = false;
                return Observed(new CdcTransportAcknowledgement());
            });
        Fake.ClearRecordedCalls(_connect);
        _trace.Clear();
    }

    private CdcControllerStatusTarget Selection() => new(_request, _runtime, 1000);

    private async Task<CdcControllerTargetStatus> Observe() =>
        (await _status.StatusAsync([Selection()])).Targets.Single();

    private Task<CdcManagedLifecycleResult> Execute(CdcManagedLifecycleOperation operation) =>
        _managed.ExecuteAsync(Selection(), operation);

    private void ReplaceWorker(bool reassign = false)
    {
        _workerEvidence = new(
            "private-recovered-jvm",
            _workerEvidence.MetricsEndpoint,
            _workerEvidence.EffectiveConfiguration,
            _workerEvidence.ImageDigest,
            _workerEvidence.HeapBytes,
            reassign ? "private-reassigned-worker:8083" : _workerEvidence.ConnectWorkerId
        );
        _assignment = _workerEvidence.ConnectWorkerId;
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_invalidates_a_recovered_pass_then_recollects_every_component(bool reassign)
    {
        (await Observe()).Status.Readiness.Should().Be(CdcReadiness.Ready);
        var before = ReadJournal();
        ReplaceWorker(reassign);
        var recovered = await Observe();
        recovered.Status.Readiness.Should().Be(CdcReadiness.NotReady);
        recovered.Recovery.Should().Be(new CdcRecoveryObservation(CdcRecoveryBoundary.NativeRecovery, true));
        recovered.Details.LagMilliseconds.Should().BeNull();
        _trace.Clear();
        var fresh = await Observe();
        fresh.Status.Readiness.Should().Be(CdcReadiness.Ready);
        fresh.Recovery.Boundary.Should().Be(CdcRecoveryBoundary.NativeRecovery);
        fresh.Recovery.RequiresFreshPass.Should().BeFalse();
        fresh.Recovery.UnobservedIntervalCertified.Should().BeFalse();
        fresh.Recovery.Explanation.Should().Contain("does not certify");
        _trace.Should().Contain(["provider", "offset", "config", "brokers", "worker", "status", "metrics"]);
        ReadJournal().Should().BeEquivalentTo(before);
        JsonSerializer
            .Serialize(fresh)
            .Should()
            .NotContain("private-recovered")
            .And.NotContain("private-reassigned");
        _barriers.Should().Be(0);
    }

    [Test]
    public async Task It_detects_task_failure_and_native_recovery_on_the_same_worker()
    {
        (await Observe()).Status.Readiness.Should().Be(CdcReadiness.Ready);
        _failed = true;
        (await Observe()).Recovery.RequiresFreshPass.Should().BeTrue();
        _failed = false;
        var recovered = await Observe();
        recovered.Recovery.RequiresFreshPass.Should().BeTrue();
        recovered.Status.Readiness.Should().Be(CdcReadiness.NotReady);
        (await Observe()).Status.Readiness.Should().Be(CdcReadiness.Ready);
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _connect.RestartAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_rejects_foreign_task_assignment_and_requires_a_fresh_pass_after_return()
    {
        await Observe();
        _assignment = "foreign-private-worker";
        var foreign = await Observe();
        foreign.Status.Readiness.Should().Be(CdcReadiness.NotReady);
        foreign.Recovery.Boundary.Should().Be(CdcRecoveryBoundary.NativeRecovery);
        _assignment = _workerEvidence.ConnectWorkerId;
        (await Observe()).Recovery.RequiresFreshPass.Should().BeTrue();
        (await Observe()).Status.Readiness.Should().Be(CdcReadiness.Ready);
    }

    [TestCase("metrics")]
    [TestCase("projection-1")]
    public async Task It_invalidates_telemetry_on_recovery_inside_a_pass(string stage)
    {
        _onCall = name =>
        {
            if (name == stage)
            {
                ReplaceWorker();
            }
        };
        var changed = await Observe();
        changed.Status.Readiness.Should().Be(CdcReadiness.NotReady);
        changed.Recovery.RequiresFreshPass.Should().BeTrue();
        changed.Diagnostics.Should().Contain(d => d.Component == CdcDeploymentComponent.Worker);
        changed.Details.LagMilliseconds.Should().BeNull();
        _onCall = _ => { };
        (await Observe()).Status.Readiness.Should().Be(CdcReadiness.Ready);
    }

    [TestCase(CdcWorkflowEffect.StopConnector, false)]
    [TestCase(CdcWorkflowEffect.StopConnector, true)]
    [TestCase(CdcWorkflowEffect.ResumeConnector, false)]
    public async Task It_routes_incomplete_or_legacy_shutdown_to_native_recovery(
        CdcWorkflowEffect effect,
        bool legacy
    )
    {
        if (legacy)
        {
            await CompleteAsync(effect);
        }
        else
        {
            await using var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                default
            );
            await session.RecordIntentAsync(
                Target,
                ReadJournal().WorkflowId,
                Guid.NewGuid(),
                effect,
                [],
                default
            );
        }
        _stopped = true;
        var first = await Observe();
        first.Recovery.Boundary.Should().Be(CdcRecoveryBoundary.NativeRecovery);
        first.Recovery.RequiresFreshPass.Should().BeTrue();
        var result = await Execute(CdcManagedLifecycleOperation.Start);
        result.Succeeded.Should().BeFalse();
        result.Boundary.Should().Be(CdcManagedLifecycleBoundary.NativeRecovery);
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_distinguishes_verified_managed_worker_restart_from_native_recovery()
    {
        await Observe();
        (await Execute(CdcManagedLifecycleOperation.Stop)).TargetShutdownVerified.Should().BeTrue();
        ReplaceWorker();
        var stopped = await Observe();
        stopped.Recovery.Boundary.Should().Be(CdcRecoveryBoundary.VerifiedManagedStop);
        stopped.Recovery.RequiresFreshPass.Should().BeFalse();
        (await Execute(CdcManagedLifecycleOperation.Start)).Succeeded.Should().BeTrue();
        var running = await Observe();
        running.Recovery.Boundary.Should().Be(CdcRecoveryBoundary.VerifiedManagedRestart);
        running.Status.Readiness.Should().Be(CdcReadiness.Ready);
        running.Recovery.UnobservedIntervalCertified.Should().BeFalse();
    }

    [Test]
    public async Task It_never_treats_native_running_state_after_a_verified_stop_as_managed_startup()
    {
        (await Execute(CdcManagedLifecycleOperation.Stop)).Succeeded.Should().BeTrue();
        _stopped = false;
        ReplaceWorker();
        var recovered = await Observe();
        recovered.Recovery.Boundary.Should().Be(CdcRecoveryBoundary.NativeRecovery);
        var startup = await Execute(CdcManagedLifecycleOperation.Start);
        startup.Succeeded.Should().BeFalse();
        startup.Boundary.Should().Be(CdcManagedLifecycleBoundary.NativeRecovery);
    }

    [TestCase("provider")]
    [TestCase("offset")]
    [TestCase("worker")]
    [TestCase("status")]
    [TestCase("metrics")]
    public async Task It_keeps_unknown_recovery_evidence_not_ready(string missing)
    {
        await Observe();
        ReplaceWorker();
        await Observe();
        _onCall = name =>
        {
            if (name == missing)
            {
                throw new IOException("private-secret");
            }
        };
        var unknown = await Observe();
        unknown.Status.Readiness.Should().Be(CdcReadiness.NotReady);
        JsonSerializer.Serialize(unknown).Should().NotContain("private-secret");
        _onCall = _ => { };
        (await Observe()).Status.Readiness.Should().Be(CdcReadiness.Ready);
    }

    [TestCase(CdcManagedLifecycleOperation.Restart)]
    [TestCase(CdcManagedLifecycleOperation.Resume)]
    public async Task It_requires_a_fresh_pass_before_controller_recovery_mutation(
        CdcManagedLifecycleOperation operation
    )
    {
        await Observe();
        ReplaceWorker();
        var first = await Execute(operation);
        first.Succeeded.Should().BeFalse();
        first.Boundary.Should().Be(CdcManagedLifecycleBoundary.NativeRecovery);
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _connect.RestartAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        (await Execute(operation)).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task It_retains_and_contains_terminal_history_after_worker_recovery()
    {
        await Observe();
        ReplaceWorker();
        _offsetState = CdcConnectOffsetState.Missing;
        var lost = await Observe();
        lost.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        lost.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Persisted);
        _stops.Should().BeGreaterThan(0);
        _offsetState = CdcConnectOffsetState.Streaming;
        _stopped = false;
        var retained = await Observe();
        retained.Status.Readiness.Should().Be(CdcReadiness.NotReady);
        retained.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        retained.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
        _stops.Should().BeGreaterThan(1);
        (await Execute(CdcManagedLifecycleOperation.Resume)).Succeeded.Should().BeFalse();
    }

    [Test]
    public async Task It_tracks_standalone_validation_without_reusing_its_previous_readiness()
    {
        async Task<CdcEstablishedValidationObservation> Validate() =>
            (
                (CdcTransportResult<CdcEstablishedValidationObservation>.Observed)
                    await _validation.ValidateAsync(
                        _request,
                        _runtime,
                        CdcEstablishedValidationMode.RunningPublication,
                        1000
                    )
            ).Value;
        (await Validate()).PublicationReady.Should().BeTrue();
        ReplaceWorker();
        var changed = await Validate();
        changed.PublicationReady.Should().BeFalse();
        changed.PreStartEligible.Should().BeFalse();
        changed.Recovery.RequiresFreshPass.Should().BeTrue();
        (await Validate()).PublicationReady.Should().BeTrue();
    }

    [Test]
    public async Task It_repeats_initial_admission_and_barrier_after_observed_offline_recovery()
    {
        _onCall = name =>
        {
            if (name == "metrics")
            {
                ReplaceWorker();
            }
        };
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
        _barriers.Should().Be(1);
        _onCall = _ => { };
        _projectionReads = 0;
        _trace.Clear();
        (await ReadyAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _barriers.Should().Be(2);
        _projectionReads.Should().Be(2);
        _trace.IndexOf("projection-1").Should().BeLessThan(_trace.IndexOf("barrier"));
        _trace.IndexOf("barrier").Should().BeLessThan(_trace.IndexOf("projection-2"));
    }

    [Test]
    public async Task It_carries_recovery_across_watch_yields_with_fresh_metrics_and_no_mutations()
    {
        List<CdcControllerTargetStatus> passes = [];
        await foreach (var result in _status.WatchAsync([Selection()], 3, TimeSpan.FromMilliseconds(1)))
        {
            passes.Add(result.Targets.Single());
            if (passes.Count == 1)
            {
                ReplaceWorker(true);
            }
        }
        passes
            .Select(p => p.Status.Readiness)
            .Should()
            .Equal(CdcReadiness.Ready, CdcReadiness.NotReady, CdcReadiness.Ready);
        passes[2].Recovery.Boundary.Should().Be(CdcRecoveryBoundary.NativeRecovery);
        passes[2].Recovery.UnobservedIntervalCertified.Should().BeFalse();
        _trace.Count(t => t == "metrics").Should().Be(2);
        _stops.Should().Be(0);
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [TestCase("provider")]
    [TestCase("offset")]
    [TestCase("worker")]
    [TestCase("status")]
    [TestCase("config")]
    [TestCase("brokers")]
    public async Task It_never_resumes_native_recovery_with_unknown_prestart_evidence(string unavailable)
    {
        await Observe();
        ReplaceWorker();
        await Observe();
        _onCall = name =>
        {
            if (name == unavailable)
            {
                throw new IOException("private-source");
            }
        };
        (await Execute(CdcManagedLifecycleOperation.Restart)).Succeeded.Should().BeFalse();
        (await Execute(CdcManagedLifecycleOperation.Resume)).Succeeded.Should().BeFalse();
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _connect.RestartAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [TestCase("workflows")]
    [TestCase("source-history")]
    public async Task It_rereads_durable_provenance_after_recovery(string directory)
    {
        await Observe();
        ReplaceWorker();
        await Observe();
        Directory.Delete(Path.Combine(_root, directory), true);
        var unavailable = await Observe();
        unavailable.Status.Readiness.Should().Be(CdcReadiness.NotReady);
        unavailable.Recovery.Boundary.Should().Be(CdcRecoveryBoundary.NativeRecovery);
        (await Execute(CdcManagedLifecycleOperation.Resume)).Succeeded.Should().BeFalse();
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_marks_stopped_state_without_a_shutdown_receipt_as_unverified()
    {
        _stopped = true;
        var result = await Observe();
        result.Recovery.Boundary.Should().Be(CdcRecoveryBoundary.NativeRecovery);
        // No earlier identity/readiness exists to invalidate; Start still requires a managed receipt.
        result.Recovery.RequiresFreshPass.Should().BeFalse();
        (await Execute(CdcManagedLifecycleOperation.Start)).Succeeded.Should().BeFalse();
    }
}
