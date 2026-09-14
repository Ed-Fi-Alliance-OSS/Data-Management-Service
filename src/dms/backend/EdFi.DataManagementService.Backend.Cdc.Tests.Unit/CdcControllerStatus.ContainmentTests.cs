// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

internal partial class Given_CdcControllerStatus
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task It_reports_recovery_after_verified_stop_as_containment_failure(bool configurationFails)
    {
        _identity = new('b', 64);
        _onCall = name =>
        {
            if (name == "worker")
            {
                _stopped = false;
            }
            if (name == "config" && configurationFails)
            {
                throw new IOException("private-config");
            }
        };
        var result = await TargetStatusAsync();
        result.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result.Status.SourceHistory.IncidentLatched.Should().BeTrue();
        result.Containment.Should().Be(CdcConnectorContainmentState.Failed);
        result
            .Diagnostics.Should()
            .Contain(d =>
                d.Component == CdcDeploymentComponent.Connect && d.Failure == CdcDeploymentFailure.Conflict
            );
        _onCall = _ => { };
        AssertContained(await TargetStatusAsync());
    }

    [Test]
    public async Task It_preserves_retained_terminal_evidence_when_both_observation_and_stop_fail()
    {
        _identity = new('b', 64);
        AssertContained(await TargetStatusAsync());
        _stopped = false;
        ShortTiming(40);
        _onCall = name =>
        {
            if (name == "provider")
            {
                throw new IOException("private-observation");
            }
        };
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Throws(new IOException("private-stop"));
        var result = await TargetStatusAsync();
        result.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result.Status.SourceHistory.IncidentLatched.Should().BeTrue();
        result.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Persisted);
        result.Containment.Should().Be(CdcConnectorContainmentState.Failed);
    }

    [Test]
    public async Task It_keeps_the_incident_durable_if_a_later_observation_is_cancelled()
    {
        _identity = new('b', 64);
        using var cancellation = new CancellationTokenSource();
        _onCall = name =>
        {
            if (name == "worker")
            {
                cancellation.Cancel();
            }
        };
        Func<Task> act = () => RunStatusAsync(cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        var state = await _bindings.ExactMatchBindingAsync(_request.Binding);
        state.State!.State.Should().Be(CdcBindingState.IncidentLatched);
        _stopped.Should().BeTrue();
    }

    [Test]
    public async Task It_contains_immediately_before_further_fallible_inspection()
    {
        _identity = new('b', 64);
        _onCall = name =>
        {
            if (name is "offset" or "worker" or "schema-history")
            {
                _stopped.Should().BeTrue();
                _stops.Should().Be(1);
            }
        };
        AssertContained(await TargetStatusAsync());
    }

    [Test]
    public async Task It_reports_real_incident_storage_failure_without_suppressing_containment()
    {
        _identity = new('b', 64);
        _onCall = name =>
        {
            if (name == "provider")
            {
                var directory = Path.Combine(_root, "incidents");
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
                File.WriteAllText(directory, "not-a-directory");
            }
        };
        var target = await TargetStatusAsync();
        target.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        target.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Failed);
        target.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
        target.Diagnostics.Should().Contain(d => d.Component == CdcDeploymentComponent.WorkflowState);
    }

    [Test]
    public async Task It_preserves_a_committed_latch_after_its_response_is_lost()
    {
        var real = _bindings;
        var faulting = A.Fake<ICdcBindingLifecycleService>();
        A.CallTo(() => faulting.ExactMatchBindingAsync(A<CdcBinding>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcBinding binding, CancellationToken token) => real.ExactMatchBindingAsync(binding, token)
            );
        A.CallTo(() => faulting.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcIncident incident, CancellationToken token) => LoseResponseAsync(incident, token)
            );
        async Task<CdcBindingLifecycleResult> LoseResponseAsync(CdcIncident incident, CancellationToken token)
        {
            await real.LatchSourceHistoryLossAsync(incident, token);
            throw new IOException("private-lost-response");
        }
        _bindings = faulting;
        ResetStatus();
        _identity = new('b', 64);
        var first = await TargetStatusAsync();
        first.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        first.Diagnostics.Should().Contain(d => d.Component == CdcDeploymentComponent.WorkflowState);
        first.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
        _bindings = real;
        ResetStatus();
        _identity = new('a', 64);
        AssertContained(await TargetStatusAsync());
    }

    [TestCase("running")]
    [TestCase("paused")]
    [TestCase("tasks-remain")]
    [TestCase("wrong-connector")]
    [TestCase("stale")]
    [TestCase("pre-stop")]
    public async Task It_does_not_accept_a_stop_acknowledgement_without_matching_stopped_readback(
        string failure
    )
    {
        _identity = new('b', 64);
        ShortTiming(50);
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                var status = CurrentStatus();
                if (!_stopped)
                {
                    return Observed(status);
                }
                return Observed(
                    failure switch
                    {
                        "running" => Status(),
                        "paused" => new CdcConnectStatus(
                            status.Runtime with
                            {
                                ConnectorState = CdcConnectorRuntimeState.Paused,
                            },
                            status.WorkerId,
                            []
                        ),
                        "tasks-remain" => new CdcConnectStatus(
                            status.Runtime,
                            status.WorkerId,
                            [new(0, CdcConnectorRuntimeState.Running, status.WorkerId)]
                        ),
                        "wrong-connector" => new CdcConnectStatus(
                            status.Runtime with
                            {
                                ConnectorName = "wrong",
                            },
                            status.WorkerId,
                            []
                        ),
                        "pre-stop" => new CdcConnectStatus(
                            status.Runtime with
                            {
                                ObservedAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1),
                            },
                            status.WorkerId,
                            []
                        ),
                        _ => new CdcConnectStatus(
                            status.Runtime with
                            {
                                ObservedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(1),
                            },
                            status.WorkerId,
                            []
                        ),
                    }
                );
            });
        var target = await TargetStatusAsync();
        target.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Persisted);
        target.Containment.Should().Be(CdcConnectorContainmentState.Failed);
        target.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        target.Diagnostics.Should().Contain(d => d.Component == CdcDeploymentComponent.Connect);
    }

    [Test]
    public async Task It_bounds_an_unresponsive_latch_and_still_stops()
    {
        var real = _bindings;
        _bindings = A.Fake<ICdcBindingLifecycleService>();
        A.CallTo(() => _bindings.ExactMatchBindingAsync(A<CdcBinding>._, A<CancellationToken>._))
            .ReturnsLazily((CdcBinding b, CancellationToken ct) => real.ExactMatchBindingAsync(b, ct));
        A.CallTo(() => _bindings.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .Returns(new TaskCompletionSource<CdcBindingLifecycleResult>().Task);
        ShortTiming(50);
        ResetStatus();
        _identity = new('b', 64);
        var target = await TargetStatusAsync();
        target.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Failed);
        target.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
        target
            .Diagnostics.Should()
            .Contain(d =>
                d.Component == CdcDeploymentComponent.WorkflowState
                && d.Failure == CdcDeploymentFailure.Timeout
            );
    }

    [Test]
    public async Task It_bounds_an_unresponsive_stop_then_reconciles_from_fresh_status()
    {
        ShortTiming(50);
        _identity = new('b', 64);
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Invokes(() => _stopped = true)
            .Returns(new TaskCompletionSource<CdcTransportResult<CdcTransportAcknowledgement>>().Task);
        AssertContained(await TargetStatusAsync());
    }

    [TestCase(CdcSqlServerSchemaHistoryState.Missing)]
    [TestCase(CdcSqlServerSchemaHistoryState.RequiredRecordLost)]
    [TestCase(CdcSqlServerSchemaHistoryState.Unknown)]
    public async Task It_latches_only_authoritative_sqlserver_schema_history_loss(
        CdcSqlServerSchemaHistoryState state
    )
    {
        A.CallTo(() => _kafka.InspectSchemaHistoryAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(Observed(state));
        var target = await TargetStatusAsync();
        if (Provider == Ddl.CdcProvider.SqlServer && state != CdcSqlServerSchemaHistoryState.Unknown)
        {
            AssertContained(target);
        }
        else
        {
            _stops.Should().Be(0);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_latches_missing_provider_history_but_keeps_unavailable_history_unknown(bool missing)
    {
        var original = _change;
        _change = r =>
        {
            var result = original(r);
            var state = missing
                ? Ddl.CdcProviderArtifactState.Missing
                : Ddl.CdcProviderArtifactState.Unavailable;
            return result with
            {
                Outcome = Ddl.CdcProviderSetupOutcome.Failed,
                ArtifactInventory = result
                    .ArtifactInventory.Select(a =>
                        a.ArtifactKind
                            is Ddl.CdcProviderArtifactKind.PostgresqlReplicationSlot
                                or Ddl.CdcProviderArtifactKind.SqlServerCaptureInstance
                            ? a with
                            {
                                State = state,
                            }
                            : a
                    )
                    .ToArray(),
            };
        };
        var target = await TargetStatusAsync();
        if (missing)
        {
            AssertContained(target);
        }
        else
        {
            target.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
            _stops.Should().Be(0);
        }
    }
}
