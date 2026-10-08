// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Tests.E2E.Cdc;
using FakeItEasy;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_CdcTerminalLossFault
{
    private ICdcConnectTransport _connect = null!;
    private readonly List<string> _calls = [];
    private CdcConnectStatus _stopped = null!;
    private CdcConnectStatus _running = null!;
    private static readonly CdcDeploymentTiming Timing = new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromMilliseconds(1)
    );

    [SetUp]
    public void Setup()
    {
        _calls.Clear();
        _connect = A.Fake<ICdcConnectTransport>();
        var runtime = new CdcConnectorRuntimeObservation(
            1,
            "operation",
            DateTimeOffset.UtcNow,
            new("deployment", "", "1", "instance", 1, CdcProvider.Postgresql),
            CdcProvider.Postgresql,
            "source",
            "connector",
            CdcConnectorRuntimeState.Stopped,
            0,
            0,
            CdcConnectorRuntimeState.Unknown,
            CdcConnectorSnapshotState.Unknown,
            null,
            null,
            []
        );
        _stopped = new(runtime, "worker", []);
        _running = new(
            runtime with
            {
                ConnectorState = CdcConnectorRuntimeState.Running,
                TaskCount = 1,
                RunningTaskCount = 1,
            },
            "worker",
            [new(0, CdcConnectorRuntimeState.Running, "worker")]
        );
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Invokes(() => _calls.Add("stop"))
            .Returns(Observed(new CdcTransportAcknowledgement()));
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Invokes(() => _calls.Add("status-stopped"))
            .Returns(Observed(_stopped));
        A.CallTo(() => _connect.DeleteOffsetsAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Invokes(() => _calls.Add("delete"))
            .Returns(Observed(new CdcTransportAcknowledgement()));
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Invokes(() => _calls.Add("missing"))
            .Returns(Observed(new CdcConnectOffsetEvidence(CdcConnectOffsetState.Missing, "", null!, null!)));
    }

    [Test]
    public async Task It_waits_for_stopped_tasks_before_exactly_one_offset_deletion_and_authoritative_readback()
    {
        int reads = 0;
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                bool running = reads++ == 0;
                _calls.Add(running ? "status-running" : "status-stopped");
                return Observed(running ? _running : _stopped);
            });
        await CdcTerminalLossFault.InjectAsync(_connect, null!, Timing, CancellationToken.None);
        _calls.Should().Equal("stop", "status-running", "status-stopped", "delete", "missing");
        A.CallTo(() => _connect.DeleteOffsetsAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [TestCase("stop")]
    [TestCase("status")]
    public async Task It_never_deletes_offsets_when_stop_is_unproven(string failure)
    {
        if (failure == "stop")
        {
            A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                .Throws<IOException>();
        }
        else
        {
            A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                .Throws<IOException>();
        }
        Func<Task> run = () =>
            CdcTerminalLossFault.InjectAsync(_connect, null!, Timing, CancellationToken.None);
        await run.Should().ThrowAsync<IOException>();
        A.CallTo(() => _connect.DeleteOffsetsAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_stopped_acknowledgement_with_remaining_tasks_with_a_finite_deadline(
        bool tasksListed
    )
    {
        var inconsistent = tasksListed
            ? new CdcConnectStatus(
                _stopped.Runtime,
                "worker",
                [new(0, CdcConnectorRuntimeState.Running, "worker")]
            )
            : new CdcConnectStatus(
                _stopped.Runtime with
                {
                    TaskCount = 1,
                    RunningTaskCount = 1,
                },
                "worker",
                []
            );
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(Observed(inconsistent));
        var shortTiming = new CdcDeploymentTiming(
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(1)
        );
        Func<Task> run = () =>
            CdcTerminalLossFault.InjectAsync(_connect, null!, shortTiming, CancellationToken.None);
        await run.Should().ThrowAsync<OperationCanceledException>();
        A.CallTo(() => _connect.DeleteOffsetsAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [TestCase(CdcConnectOffsetState.Streaming)]
    [TestCase(CdcConnectOffsetState.Null)]
    [TestCase(CdcConnectOffsetState.Malformed)]
    public async Task It_requires_authoritative_missing_evidence_after_deletion(CdcConnectOffsetState state)
    {
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(Observed(new CdcConnectOffsetEvidence(state, "", null!, null!)));
        Func<Task> run = () =>
            CdcTerminalLossFault.InjectAsync(_connect, null!, Timing, CancellationToken.None);
        await run.Should().ThrowAsync<AssertionException>();
        A.CallTo(() => _connect.DeleteOffsetsAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    private static CdcTransportResult<T> Observed<T>(T value)
        where T : notnull => new CdcTransportResult<T>.Observed(value);
}

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
public class Given_CdcTerminalLossAssertions(CdcProvider provider)
{
    private CdcBinding _binding = null!;
    private CdcControllerTargetStatus _lost = null!;
    private CdcBindingLifecycleResult _retained = null!;

    [SetUp]
    public void Setup()
    {
        var now = DateTimeOffset.UtcNow;
        _binding = new(
            1,
            "deployment",
            "",
            "1",
            "instance",
            1,
            provider,
            "source",
            "connector",
            "public",
            3,
            "kafka-murmur2-v1",
            1
        );
        var satisfied = CdcComponent.Satisfied(now);
        _lost = new(
            now,
            new(
                _binding.ToTargetIdentity(),
                CdcReadiness.NotReady,
                CdcBlockingCategory.SourceHistoryLost,
                satisfied,
                satisfied,
                satisfied,
                satisfied,
                CdcSourceHistoryComponent.FromComponent(
                    CdcComponent.NotSatisfied(CdcBlockingCategory.SourceHistoryLost, now),
                    CdcSourceHistoryContinuity.Lost,
                    true
                ),
                satisfied,
                satisfied,
                satisfied,
                satisfied,
                satisfied,
                []
            ),
            new(
                DocumentCacheStatusQueuePresence.Empty,
                0,
                100,
                0,
                0,
                0,
                CdcProviderArtifactContinuityState.ExactMatch,
                CdcProviderRetainedRangeState.Unknown,
                CdcSqlServerSchemaHistoryState.NotApplicable,
                CdcIncidentFailureCategory.ConnectOffsetMissing,
                new("", "", "", null, "", "", [])
            ),
            false,
            false,
            CdcIncidentPersistenceState.Persisted,
            CdcConnectorContainmentState.Stopped,
            []
        );
        _retained = new(
            1,
            now,
            CdcControlPlaneOperationStatus.Succeeded,
            new(
                1,
                now,
                CdcBindingState.IncidentLatched,
                _binding,
                new(
                    1,
                    CdcIncidentType.SourceHistoryContinuityLost,
                    now,
                    _binding.ToCompleteBindingIdentity(),
                    CdcIncidentFailureCategory.ConnectOffsetMissing,
                    new(
                        "connector",
                        "public",
                        "progress",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        []
                    )
                )
            ),
            []
        );
    }

    [Test]
    public void It_accepts_durable_loss_containment_and_both_lifecycle_rejections()
    {
        CdcTerminalLossAssertions.AssertLost(_lost);
        CdcTerminalLossAssertions
            .AssertRetained(_retained, _binding)
            .Should()
            .BeSameAs(_retained.State!.Incident);
        foreach (
            var operation in new[]
            {
                CdcManagedLifecycleOperation.Restart,
                CdcManagedLifecycleOperation.Resume,
            }
        )
        {
            CdcTerminalLossAssertions.AssertRejected(Rejected(operation), operation);
        }
    }

    [TestCase("ready")]
    [TestCase("healthy")]
    [TestCase("unlatched")]
    [TestCase("unpersisted")]
    [TestCase("uncontained")]
    [TestCase("reason")]
    [TestCase("category")]
    public void It_rejects_incomplete_or_unrelated_loss_evidence(string defect)
    {
        var broken = defect switch
        {
            "ready" => _lost with { Status = _lost.Status with { Readiness = CdcReadiness.Ready } },
            "healthy" => _lost with
            {
                Status = _lost.Status with
                {
                    SourceHistory = _lost.Status.SourceHistory with
                    {
                        Continuity = CdcSourceHistoryContinuity.Healthy,
                    },
                },
            },
            "unlatched" => _lost with
            {
                Status = _lost.Status with
                {
                    SourceHistory = _lost.Status.SourceHistory with { IncidentLatched = false },
                },
            },
            "unpersisted" => _lost with { IncidentPersistence = CdcIncidentPersistenceState.Failed },
            "uncontained" => _lost with { Containment = CdcConnectorContainmentState.Failed },
            "reason" => _lost with
            {
                Details = _lost.Details with
                {
                    IncidentFailureCategory = CdcIncidentFailureCategory.ProviderArtifactMissing,
                },
            },
            _ => _lost with
            {
                Status = _lost.Status with
                {
                    SourceHistory = _lost.Status.SourceHistory with
                    {
                        Category = CdcBlockingCategory.ProviderHistoryUnknown,
                    },
                },
            },
        };
        Action check = () => CdcTerminalLossAssertions.AssertLost(broken);
        check.Should().Throw<AssertionException>();
    }

    [TestCase("missing")]
    [TestCase("binding")]
    [TestCase("incident-binding")]
    [TestCase("reason")]
    [TestCase("state")]
    public void It_rejects_missing_changed_or_wrong_binding_incident(string defect)
    {
        var state = _retained.State!;
        var broken = _retained with
        {
            State = defect switch
            {
                "missing" => state with { Incident = null },
                "binding" => state with { Binding = _binding with { Generation = 2 } },
                "incident-binding" => state with
                {
                    Incident = state.Incident! with
                    {
                        BindingIdentity = (_binding with { Generation = 2 }).ToCompleteBindingIdentity(),
                    },
                },
                "reason" => state with
                {
                    Incident = state.Incident! with
                    {
                        FailureCategory = CdcIncidentFailureCategory.ProviderArtifactMissing,
                    },
                },
                _ => state with { State = CdcBindingState.BindingPresent },
            },
        };
        Action check = () => CdcTerminalLossAssertions.AssertRetained(broken, _binding);
        check.Should().Throw<AssertionException>();
    }

    [TestCase("success")]
    [TestCase("ready")]
    [TestCase("operation")]
    [TestCase("missing-observation")]
    public void It_rejects_false_lifecycle_rejection(string defect)
    {
        var result = Rejected(CdcManagedLifecycleOperation.Resume);
        result = defect switch
        {
            "success" => result with { Succeeded = true },
            "ready" => result with { Ready = true },
            "operation" => result with { Operation = CdcManagedLifecycleOperation.Stop },
            _ => result with { Observation = null! },
        };
        Action check = () =>
            CdcTerminalLossAssertions.AssertRejected(result, CdcManagedLifecycleOperation.Resume);
        check.Should().Throw<AssertionException>();
    }

    private CdcManagedLifecycleResult Rejected(CdcManagedLifecycleOperation operation) =>
        new(operation, false, false, false, CdcManagedLifecycleBoundary.NativeRecovery, [])
        {
            Observation = _lost,
        };
}
