// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Tests.E2E.Cdc;
using FakeItEasy;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_CdcOffsetEvidenceTransport
{
    private ICdcConnectTransport _inner = null!;
    private CdcOffsetEvidenceTransport _transport = null!;
    private CdcTransportResult<CdcConnectOffsetEvidence> _evidence = null!;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);

    [SetUp]
    public void Setup()
    {
        _inner = A.Fake<ICdcConnectTransport>();
        _transport = new(_inner);
        _evidence = new CdcTransportResult<CdcConnectOffsetEvidence>.Observed(
            new(CdcConnectOffsetState.Streaming, "partition", null!, null!)
        );
        A.CallTo(() => _inner.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(_evidence);
    }

    [Test]
    public async Task It_fails_only_offset_evidence_and_forwards_every_other_call_with_its_token()
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        await _transport.RunUnavailableAsync(
            async faultToken =>
            {
                Func<Task> read = () => _transport.ReadOffsetEvidenceAsync(null!, faultToken);
                await read.Should().ThrowAsync<IOException>();
                _transport.UnavailableReads.Should().Be(1);
                A.CallTo(() =>
                        _inner.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._)
                    )
                    .MustNotHaveHappened();
                await _transport.ReadConfigurationAsync(null!, token);
                await _transport.ValidateConfigurationAsync(null!, null!, token);
                await _transport.CreateAsync(null!, null!, token);
                await _transport.ReadOffsetsAsync(null!, token);
                await _transport.ReadStatusAsync(null!, token);
                await _transport.UpdateConfigurationForRecordSizeIncreaseAsync(null!, null!, token);
                await _transport.ResumeAsync(null!, token);
                await _transport.DeleteOffsetsAsync(null!, token);
                await _transport.RestartAsync(null!, token);
                await _transport.StopAsync(null!, token);
                await _transport.DeleteAsync(null!, token);
            },
            Duration,
            token
        );
        A.CallTo(() => _inner.ReadConfigurationAsync(null!, token)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.ValidateConfigurationAsync(null!, null!, token)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.CreateAsync(null!, null!, token)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.ReadOffsetsAsync(null!, token)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.ReadStatusAsync(null!, token)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.UpdateConfigurationForRecordSizeIncreaseAsync(null!, null!, token))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.ResumeAsync(null!, token)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.DeleteOffsetsAsync(null!, token)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.RestartAsync(null!, token)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.StopAsync(null!, token)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.DeleteAsync(null!, token)).MustHaveHappenedOnceExactly();
        (await _transport.ReadOffsetEvidenceAsync(null!, token)).Should().BeSameAs(_evidence);
        A.CallTo(() => _inner.ReadOffsetEvidenceAsync(null!, token)).MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_offers_captured_healthy_offsets_without_changing_real_missing_evidence()
    {
        var captured = ((CdcTransportResult<CdcConnectOffsetEvidence>.Observed)_evidence).Value;
        var missing = new CdcTransportResult<CdcConnectOffsetEvidence>.Observed(
            new(CdcConnectOffsetState.Missing, "", null!, null!)
        );
        A.CallTo(() => _inner.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(missing);
        await _transport.RunWithHealthyEvidenceAsync(
            captured,
            async token =>
            {
                var replay = (CdcTransportResult<CdcConnectOffsetEvidence>.Observed)
                    await _transport.ReadOffsetEvidenceAsync(null!, token);
                replay.Value.Should().BeSameAs(captured);
                _transport.ReplayedReads.Should().Be(1);
                _transport.UnavailableReads.Should().Be(0);
                await _transport.ReadStatusAsync(null!, token);
                await _transport.StopAsync(null!, token);
                A.CallTo(() => _inner.ReadStatusAsync(null!, token)).MustHaveHappenedOnceExactly();
                A.CallTo(() => _inner.StopAsync(null!, token)).MustHaveHappenedOnceExactly();
                A.CallTo(() =>
                        _inner.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._)
                    )
                    .MustNotHaveHappened();
            },
            Duration,
            CancellationToken.None
        );
        (await _transport.ReadOffsetEvidenceAsync(null!, CancellationToken.None)).Should().BeSameAs(missing);
        A.CallTo(() => _inner.DeleteOffsetsAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        _transport.StartCalls.Should().Be(0);
        await _transport.ResumeAsync(null!, CancellationToken.None);
        await _transport.RestartAsync(null!, CancellationToken.None);
        _transport.StartCalls.Should().Be(2);
    }

    [TestCase("failure")]
    [TestCase("deadline")]
    [TestCase("cancellation")]
    public async Task It_restores_healthy_replay_on_every_exit(string exit)
    {
        using var caller = new CancellationTokenSource();
        var captured = ((CdcTransportResult<CdcConnectOffsetEvidence>.Observed)_evidence).Value;
        Func<Task> run = () =>
            _transport.RunWithHealthyEvidenceAsync(
                captured,
                async token =>
                {
                    if (exit == "failure")
                    {
                        throw new IOException();
                    }
                    if (exit == "cancellation")
                    {
                        await caller.CancelAsync();
                    }
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Even an action ignoring cancellation must no longer see replayed evidence.
                        (await _transport.ReadOffsetEvidenceAsync(null!, CancellationToken.None))
                            .Should()
                            .BeSameAs(_evidence);
                    }
                },
                exit == "deadline" ? TimeSpan.FromMilliseconds(30) : Duration,
                caller.Token
            );
        if (exit == "failure")
        {
            await run.Should().ThrowAsync<IOException>();
        }
        else
        {
            await run.Should().ThrowAsync<OperationCanceledException>();
        }
        (await _transport.ReadOffsetEvidenceAsync(null!, CancellationToken.None))
            .Should()
            .BeSameAs(_evidence);
    }

    [Test]
    public async Task It_rejects_replay_during_the_unavailable_fault()
    {
        var captured = ((CdcTransportResult<CdcConnectOffsetEvidence>.Observed)_evidence).Value;
        await _transport.RunUnavailableAsync(
            async token =>
            {
                Func<Task> nested = () =>
                    _transport.RunWithHealthyEvidenceAsync(
                        captured,
                        _ => Task.CompletedTask,
                        Duration,
                        token
                    );
                await nested.Should().ThrowAsync<InvalidOperationException>();
                Func<Task> read = () => _transport.ReadOffsetEvidenceAsync(null!, token);
                await read.Should().ThrowAsync<IOException>();
            },
            Duration,
            CancellationToken.None
        );
    }

    [Test]
    public void It_rejects_unhealthy_replay_evidence()
    {
        Action run = () =>
            _transport.RunWithHealthyEvidenceAsync(
                new(CdcConnectOffsetState.Missing, "", null!, null!),
                _ => Task.CompletedTask,
                Duration,
                CancellationToken.None
            );
        run.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task It_restores_real_delegation_after_callback_failure()
    {
        Func<Task> run = () =>
            _transport.RunUnavailableAsync(
                _ => throw new InvalidOperationException(),
                Duration,
                CancellationToken.None
            );
        await run.Should().ThrowAsync<InvalidOperationException>();
        (await _transport.ReadOffsetEvidenceAsync(null!, CancellationToken.None))
            .Should()
            .BeSameAs(_evidence);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_restores_on_deadline_or_caller_cancellation_even_before_callback_returns(
        bool cancelCaller
    )
    {
        using var caller = new CancellationTokenSource();
        Func<Task> run = () =>
            _transport.RunUnavailableAsync(
                async token =>
                {
                    if (cancelCaller)
                    {
                        await caller.CancelAsync();
                    }
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Deliberately ignore cancellation once: the expired fault must already delegate,
                        // and RunUnavailableAsync must still reject a nominally successful callback.
                        (await _transport.ReadOffsetEvidenceAsync(null!, CancellationToken.None))
                            .Should()
                            .BeSameAs(_evidence);
                    }
                },
                cancelCaller ? Duration : TimeSpan.FromMilliseconds(30),
                caller.Token
            );
        await run.Should().ThrowAsync<OperationCanceledException>();
        (await _transport.ReadOffsetEvidenceAsync(null!, CancellationToken.None))
            .Should()
            .BeSameAs(_evidence);
    }

    [Test]
    public async Task It_rejects_overlapping_faults_without_restoring_the_outer_fault()
    {
        await _transport.RunUnavailableAsync(
            async token =>
            {
                Func<Task> nested = () =>
                    _transport.RunUnavailableAsync(_ => Task.CompletedTask, Duration, token);
                await nested.Should().ThrowAsync<InvalidOperationException>();
                Func<Task> read = () => _transport.ReadOffsetEvidenceAsync(null!, token);
                await read.Should().ThrowAsync<IOException>();
            },
            Duration,
            CancellationToken.None
        );
        (await _transport.ReadOffsetEvidenceAsync(null!, CancellationToken.None))
            .Should()
            .BeSameAs(_evidence);
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(301)]
    public async Task It_rejects_unbounded_or_invalid_fault_intervals(int seconds)
    {
        Func<Task> run = () =>
            _transport.RunUnavailableAsync(
                _ => Task.CompletedTask,
                TimeSpan.FromSeconds(seconds),
                CancellationToken.None
            );
        await run.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task It_preserves_cancellation_instead_of_reporting_unavailable()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        Func<Task> read = () => _transport.ReadOffsetEvidenceAsync(null!, caller.Token);
        await read.Should().ThrowAsync<OperationCanceledException>();
        _transport.UnavailableReads.Should().Be(0);
        A.CallTo(() => _inner.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_preserves_forwarded_results_and_exceptions()
    {
        var failure = new CdcTransportResult<JsonElement>.Unavailable(
            new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.AuthenticationFailed)
        );
        A.CallTo(() => _inner.ReadOffsetsAsync(null!, CancellationToken.None)).Returns(failure);
        (await _transport.ReadOffsetsAsync(null!, CancellationToken.None)).Should().BeSameAs(failure);
        A.CallTo(() => _inner.ReadOffsetEvidenceAsync(null!, CancellationToken.None)).Throws<IOException>();
        Func<Task> read = () => _transport.ReadOffsetEvidenceAsync(null!, CancellationToken.None);
        await read.Should().ThrowAsync<IOException>();
        _transport.UnavailableReads.Should().Be(0);
    }
}

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
public class Given_CdcUnavailableEvidenceAssertions(CdcProvider provider)
{
    private CdcBinding _binding = null!;
    private CdcControllerTargetStatus _unknown = null!;
    private CdcControllerTargetStatus _healthy = null!;
    private DateTimeOffset _now;

    [SetUp]
    public void Setup()
    {
        _now = DateTimeOffset.UtcNow;
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
        var satisfied = CdcComponent.Satisfied(_now);
        var status = new CdcTargetStatus(
            _binding.ToTargetIdentity(),
            CdcReadiness.NotReady,
            CdcBlockingCategory.ConnectorConfigInvalid,
            satisfied,
            satisfied,
            satisfied,
            satisfied,
            CdcSourceHistoryComponent.FromComponent(
                CdcComponent.Unknown(CdcBlockingCategory.ProviderHistoryUnknown, _now),
                CdcSourceHistoryContinuity.Unknown,
                false
            ),
            satisfied,
            satisfied,
            CdcComponent.NotSatisfied(CdcBlockingCategory.ConnectorConfigInvalid, _now),
            CdcComponent.Unknown(CdcBlockingCategory.StatusObservationUnavailable, _now),
            satisfied,
            []
        );
        _unknown = new(
            _now,
            status,
            new(
                DocumentCacheStatusQueuePresence.Empty,
                0,
                100,
                0,
                0,
                0,
                CdcProviderArtifactContinuityState.ExactMatch,
                CdcProviderRetainedRangeState.CoversCommittedOffset,
                CdcSqlServerSchemaHistoryState.NotApplicable,
                null,
                new("", "", "", null, "", "", [CdcIncidentUnavailableFact.ConnectOffset])
            ),
            false,
            false,
            CdcIncidentPersistenceState.NotRequired,
            CdcConnectorContainmentState.NotRequired,
            [new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)]
        );
        _healthy = _unknown with
        {
            Status = status with
            {
                Readiness = CdcReadiness.Ready,
                PrimaryBlockingCategory = CdcBlockingCategory.None,
                ConnectorConfig = satisfied,
                ConnectorRuntime = satisfied,
                SourceHistory = CdcSourceHistoryComponent.FromComponent(
                    satisfied,
                    CdcSourceHistoryContinuity.Healthy,
                    false
                ),
            },
            Diagnostics = [],
        };
    }

    [TestCase(CdcManagedLifecycleOperation.Restart)]
    [TestCase(CdcManagedLifecycleOperation.Resume)]
    public void It_accepts_rejection_from_offset_unavailability(CdcManagedLifecycleOperation operation) =>
        CdcUnavailableEvidenceAssertions.AssertRejected(Rejected(operation, _unknown), operation);

    [TestCase(CdcManagedLifecycleOperation.Restart, false)]
    [TestCase(CdcManagedLifecycleOperation.Resume, false)]
    [TestCase(CdcManagedLifecycleOperation.Restart, true)]
    [TestCase(CdcManagedLifecycleOperation.Resume, true)]
    public async Task It_requires_no_forwarded_start_effect_during_unavailable_evidence(
        CdcManagedLifecycleOperation operation,
        bool forwardStart
    )
    {
        var inner = A.Fake<ICdcConnectTransport>();
        var transport = new CdcOffsetEvidenceTransport(inner);
        // Earlier healthy operations must not invalidate the interval-specific assertion.
        await transport.ResumeAsync(null!, CancellationToken.None);
        int before = transport.StartCalls;
        await transport.RunUnavailableAsync(
            async token =>
            {
                CdcUnavailableEvidenceAssertions.AssertUnknown(_unknown);
                CdcUnavailableEvidenceAssertions.AssertNoConnectorStart(before, transport.StartCalls);
                if (forwardStart)
                {
                    if (operation == CdcManagedLifecycleOperation.Restart)
                    {
                        await transport.RestartAsync(null!, token);
                        A.CallTo(() => inner.RestartAsync(null!, token)).MustHaveHappenedOnceExactly();
                    }
                    else
                    {
                        await transport.ResumeAsync(null!, token);
                        A.CallTo(() => inner.ResumeAsync(null!, token)).MustHaveHappenedOnceExactly();
                    }
                }

                // A correct rejection result alone cannot rule out an earlier transport effect.
                CdcUnavailableEvidenceAssertions.AssertRejected(Rejected(operation, _unknown), operation);
                Action check = () =>
                    CdcUnavailableEvidenceAssertions.AssertNoConnectorStart(before, transport.StartCalls);
                if (forwardStart)
                {
                    check.Should().Throw<AssertionException>();
                }
                else
                {
                    check.Should().NotThrow();
                    A.CallTo(() => inner.RestartAsync(null!, token)).MustNotHaveHappened();
                    A.CallTo(() => inner.ResumeAsync(null!, token)).MustNotHaveHappened();
                }
            },
            TimeSpan.FromSeconds(5),
            CancellationToken.None
        );

        // Recovery is outside the measured interval and may resume the real connector.
        int after = transport.StartCalls;
        await transport.ResumeAsync(null!, CancellationToken.None);
        transport.StartCalls.Should().Be(after + 1);
        A.CallTo(() => inner.ResumeAsync(null!, CancellationToken.None)).MustHaveHappenedTwiceExactly();
    }

    [TestCase("healthy")]
    [TestCase("latched")]
    [TestCase("wrong-evidence")]
    [TestCase("missing-reason")]
    [TestCase("provider")]
    [TestCase("binding")]
    [TestCase("kafka")]
    [TestCase("offset-store")]
    [TestCase("worker")]
    [TestCase("pending-resize")]
    [TestCase("shared-store")]
    public void It_rejects_unrelated_prerequisites_or_missing_continuity_reason(string defect)
    {
        var unavailable = CdcComponent.Unknown(CdcBlockingCategory.StatusObservationUnavailable, _now);
        var broken = defect switch
        {
            "healthy" => _healthy,
            "latched" => _unknown with
            {
                Status = _unknown.Status with
                {
                    SourceHistory = _unknown.Status.SourceHistory with { IncidentLatched = true },
                },
            },
            "wrong-evidence" => _unknown with
            {
                Details = _unknown.Details with
                {
                    Positions = _unknown.Details.Positions with
                    {
                        UnavailableFacts = [CdcIncidentUnavailableFact.ProviderArtifact],
                    },
                },
            },
            "missing-reason" => _unknown with { Diagnostics = [] },
            "provider" => _unknown with { Status = _unknown.Status with { ProviderSetup = unavailable } },
            "binding" => _unknown with { Status = _unknown.Status with { Binding = unavailable } },
            "kafka" => _unknown with { Status = _unknown.Status with { KafkaPolicy = unavailable } },
            "offset-store" => _unknown with
            {
                Status = _unknown.Status with { ConnectOffsetStore = unavailable },
            },
            "worker" => _unknown with
            {
                Status = _unknown.Status with
                {
                    ConnectorRuntime = CdcComponent.NotSatisfied(
                        CdcBlockingCategory.ConnectorNotRunning,
                        _now
                    ),
                },
            },
            "pending-resize" => _unknown with { HasPendingRecordSizeIncrease = true },
            _ => _unknown with { HasSharedOffsetStoreIssue = true },
        };
        Action check = () =>
            CdcUnavailableEvidenceAssertions.AssertRejected(
                Rejected(CdcManagedLifecycleOperation.Resume, broken),
                CdcManagedLifecycleOperation.Resume
            );
        check.Should().Throw<AssertionException>();
    }

    [TestCase("success")]
    [TestCase("ready")]
    [TestCase("operation")]
    [TestCase("no-observation")]
    [TestCase("no-reason")]
    public void It_rejects_false_lifecycle_rejection_evidence(string defect)
    {
        var result = Rejected(CdcManagedLifecycleOperation.Resume, _unknown);
        result = defect switch
        {
            "success" => result with { Succeeded = true },
            "ready" => result with { Ready = true },
            "operation" => result with { Operation = CdcManagedLifecycleOperation.Stop },
            "no-observation" => result with { Observation = null! },
            _ => result with { Diagnostics = [] },
        };
        Action check = () =>
            CdcUnavailableEvidenceAssertions.AssertRejected(result, CdcManagedLifecycleOperation.Resume);
        check.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_accepts_fresh_affirmative_recovery_on_the_same_binding() =>
        CdcUnavailableEvidenceAssertions.AssertHealthy(_healthy, _binding, _now);

    [TestCase("stale")]
    [TestCase("stale-continuity")]
    [TestCase("generation")]
    [TestCase("not-ready")]
    public void It_rejects_stale_or_different_binding_recovery(string defect)
    {
        var observation = defect switch
        {
            "stale" => _healthy with { ObservedAt = _now.AddSeconds(-1) },
            "stale-continuity" => _healthy with
            {
                Status = _healthy.Status with
                {
                    SourceHistory = _healthy.Status.SourceHistory with { ObservedAt = _now.AddSeconds(-1) },
                },
            },
            "generation" => _healthy with
            {
                Status = _healthy.Status with
                {
                    TargetIdentity = (_binding with { Generation = 2 }).ToTargetIdentity(),
                },
            },
            _ => _unknown,
        };
        Action check = () => CdcUnavailableEvidenceAssertions.AssertHealthy(observation, _binding, _now);
        check.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_rejects_retained_terminal_state_or_changed_topic()
    {
        var result = new CdcBindingLifecycleResult(
            1,
            _now,
            CdcControlPlaneOperationStatus.Succeeded,
            new(1, _now, CdcBindingState.BindingPresent, _binding, null),
            []
        );
        CdcUnavailableEvidenceAssertions.AssertRetained(result, _binding);
        Action terminal = () =>
            CdcUnavailableEvidenceAssertions.AssertRetained(
                result with
                {
                    State = result.State! with { State = CdcBindingState.IncidentLatched },
                },
                _binding
            );
        terminal.Should().Throw<AssertionException>();
        Action changed = () =>
            CdcUnavailableEvidenceAssertions.AssertRetained(
                result with
                {
                    State = result.State! with { Binding = _binding with { TopicName = "different" } },
                },
                _binding
            );
        changed.Should().Throw<AssertionException>();
    }

    private static CdcManagedLifecycleResult Rejected(
        CdcManagedLifecycleOperation operation,
        CdcControllerTargetStatus observation
    ) =>
        new(
            operation,
            false,
            false,
            false,
            CdcManagedLifecycleBoundary.NativeRecovery,
            [new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)]
        )
        {
            Observation = observation,
        };
}
