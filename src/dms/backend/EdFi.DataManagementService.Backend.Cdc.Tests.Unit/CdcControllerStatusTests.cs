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
internal partial class Given_CdcControllerStatus(Ddl.CdcProvider provider) : CdcReadinessTestBase(provider)
{
    private CdcControllerStatus _status = null!;
    private ICdcBindingLifecycleService _bindings = null!;
    private bool _stopped;
    private int _stops;

    [SetUp]
    public void SetupStatus()
    {
        _bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        _stopped = false;
        _stops = 0;
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("stop");
                _stops++;
                _stopped = true;
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("status");
                return Observed(CurrentStatus());
            });
        _trace.Clear();
        _calls.Clear();
        Fake.ClearRecordedCalls(_connect);
        Fake.ClearRecordedCalls(_runtime);
        Fake.ClearRecordedCalls(_kafka);
        ResetStatus();
    }

    [TearDown]
    public void It_performs_only_observation_and_terminal_containment()
    {
        Fake.GetCalls(_connect)
            .Should()
            .NotContain(c =>
                !c.Method.Name.StartsWith("Read", StringComparison.Ordinal) && c.Method.Name != "StopAsync"
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

    private CdcConnectStatus CurrentStatus()
    {
        var current = Status();
        return !_stopped
            ? current
            : new(
                current.Runtime with
                {
                    ConnectorState = CdcConnectorRuntimeState.Stopped,
                    SoleTaskState = CdcConnectorRuntimeState.Stopped,
                    TaskCount = 0,
                    RunningTaskCount = 0,
                },
                current.WorkerId,
                []
            );
    }

    private CdcEstablishedValidation Validation() =>
        new(
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

    private void ResetStatus() =>
        _status = new(_store, _bindings, _connect, _ => Validation(), TimeProvider.System);

    private CdcControllerStatusTarget Selection() => new(_request, _runtime, 1000);

    private Task<CdcControllerStatusResult> RunStatusAsync(CancellationToken token = default) =>
        _status.StatusAsync([Selection()], token);

    private async Task<CdcControllerTargetStatus> TargetStatusAsync() =>
        (await RunStatusAsync()).Targets.Single();

    [Test]
    public async Task It_reports_fresh_component_health_without_a_new_barrier_or_durable_readiness()
    {
        var before = Snapshot();
        var result = await RunStatusAsync();
        result.Aggregate.Readiness.Should().Be(CdcReadiness.Ready);
        var target = result.Targets.Single();
        target.Status.ProviderBarrier.State.Should().Be(CdcComponentState.NotApplicable);
        target.Details.QueuePresence.Should().Be(DocumentCacheStatusQueuePresence.Empty);
        target.Details.LagMilliseconds.Should().Be(1);
        target.Details.P95LagMilliseconds.Should().BeNull();
        target.Details.RetainedRangeState.Should().Be(CdcProviderRetainedRangeState.CoversCommittedOffset);
        target.Details.Positions.RetainedRangeStart.Should().NotBeEmpty();
        (
            Provider == Ddl.CdcProvider.Postgresql
                ? target.Details.Positions.LsnProc
                : target.Details.Positions.CommitLsn
        )
            .Should()
            .NotBeEmpty();
        target.Diagnostics.Should().BeEmpty();
        target.Containment.Should().Be(CdcConnectorContainmentState.NotRequired);
        Snapshot().Should().BeEquivalentTo(before);
        var second = await RunStatusAsync();
        second.Targets.Single().ObservedAt.Should().BeAfter(target.ObservedAt);
        _projectionReads.Should().Be(2);
    }

    private Dictionary<string, byte[]> Snapshot() =>
        Directory
            .GetFiles(_root, "*.json", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.ReadAllBytes);

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_reports_queue_and_lag_failures_independently(bool backlog)
    {
        _backlog = backlog;
        _lag = backlog ? 1 : 1001;
        var target = await TargetStatusAsync();
        target.Status.Readiness.Should().Be(CdcReadiness.NotReady);
        target.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Healthy);
        target
            .Details.QueuePresence.Should()
            .Be(backlog ? DocumentCacheStatusQueuePresence.NotEmpty : DocumentCacheStatusQueuePresence.Empty);
        target.Details.LagMilliseconds.Should().Be(_lag);
        _stops.Should().Be(0);
    }

    [TestCase(CdcConnectOffsetState.Missing)]
    [TestCase(CdcConnectOffsetState.Null)]
    [TestCase(CdcConnectOffsetState.Malformed)]
    [TestCase(CdcConnectOffsetState.Snapshot)]
    [TestCase(CdcConnectOffsetState.Multiple)]
    public async Task It_latches_authoritative_offset_loss_and_verifies_connector_stop(
        CdcConnectOffsetState state
    )
    {
        _offsetState = state;
        var target = await TargetStatusAsync();
        AssertContained(target);
        _stops.Should().Be(1);
        var persisted = await _bindings.ExactMatchBindingAsync(_request.Binding);
        persisted.State!.State.Should().Be(CdcBindingState.IncidentLatched);
        target.Details.IncidentFailureCategory.Should().NotBeNull();
        target.Status.ConnectorRuntime.State.Should().Be(CdcComponentState.NotSatisfied);
    }

    private static void AssertContained(CdcControllerTargetStatus target)
    {
        target.Status.Readiness.Should().Be(CdcReadiness.NotReady);
        target.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        target.Status.SourceHistory.IncidentLatched.Should().BeTrue();
        target.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Persisted);
        target.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
    }

    [Test]
    public async Task It_never_clears_a_retained_incident_after_artifacts_and_offsets_recover()
    {
        _identity = new('b', 64);
        AssertContained(await TargetStatusAsync());
        var before = Snapshot();
        _identity = new('a', 64);
        _stopped = false;
        ResetStatus();
        AssertContained(await TargetStatusAsync());
        Snapshot().Should().BeEquivalentTo(before);
        _stops.Should().Be(2);
    }

    [TestCase("provider")]
    [TestCase("offset")]
    [TestCase("schema-history")]
    [TestCase("worker")]
    [TestCase("config")]
    [TestCase("topic")]
    [TestCase("metrics")]
    public async Task It_contains_retained_loss_even_when_current_inspection_throws(string stage)
    {
        _identity = new('b', 64);
        AssertContained(await TargetStatusAsync());
        _identity = new('a', 64);
        _stopped = false;
        _onCall = name =>
        {
            if (name == stage)
            {
                throw new IOException("private-source-and-password");
            }
        };
        AssertContained(await TargetStatusAsync());
    }

    [TestCase("offset")]
    [TestCase("schema-history")]
    [TestCase("worker")]
    [TestCase("config")]
    [TestCase("topic")]
    [TestCase("metrics")]
    public async Task It_keeps_new_provider_loss_when_a_later_transport_throws(string stage)
    {
        _identity = new('b', 64);
        _onCall = name =>
        {
            if (name == stage)
            {
                throw new IOException("private-source-and-password");
            }
        };
        AssertContained(await TargetStatusAsync());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_classifies_and_contains_loss_even_when_projection_is_ineligible(bool cacheAhead)
    {
        _identity = new('b', 64);
        A.CallTo(() => _runtime.ObserveInitialDatabaseAsync(A<CancellationToken>._))
            .Throws(new IOException("unavailable projection"));
        _latch = cacheAhead;
        AssertContained(await TargetStatusAsync());
    }

    [Test]
    public async Task It_reports_unknown_continuity_without_latching_or_stopping()
    {
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(
                new CdcTransportResult<CdcConnectOffsetEvidence>.Unavailable(
                    new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.AuthenticationFailed)
                )
            );
        var target = await TargetStatusAsync();
        target.Status.Readiness.Should().NotBe(CdcReadiness.Ready);
        target.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        target.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.NotRequired);
        _stops.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_reports_failed_latch_and_still_attempts_containment(bool alsoFailStop)
    {
        var real = _bindings;
        _bindings = A.Fake<ICdcBindingLifecycleService>();
        A.CallTo(() => _bindings.ExactMatchBindingAsync(A<CdcBinding>._, A<CancellationToken>._))
            .ReturnsLazily((CdcBinding b, CancellationToken ct) => real.ExactMatchBindingAsync(b, ct));
        A.CallTo(() => _bindings.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .Throws(new IOException("private-latch-failure"));
        if (alsoFailStop)
        {
            A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                .Invokes(() => _stops++)
                .Throws(new IOException("private-stop-failure"));
            ShortTiming(40);
        }
        ResetStatus();
        _identity = new('b', 64);
        var target = await TargetStatusAsync();
        target.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        target.Status.SourceHistory.IncidentLatched.Should().BeFalse();
        target.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Failed);
        target
            .Containment.Should()
            .Be(alsoFailStop ? CdcConnectorContainmentState.Failed : CdcConnectorContainmentState.Stopped);
        target.Diagnostics.Should().Contain(d => d.Component == CdcDeploymentComponent.WorkflowState);
        if (alsoFailStop)
        {
            target.Diagnostics.Should().Contain(d => d.Component == CdcDeploymentComponent.Connect);
        }
        _stops.Should().Be(1);
        JsonSerializer.Serialize(target).Should().NotContain("private-");
    }

    [Test]
    public async Task It_reconciles_a_lost_stop_response_from_fresh_stopped_status()
    {
        _identity = new('b', 64);
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Invokes(() => _stopped = true)
            .Throws(new TimeoutException());
        var target = await TargetStatusAsync();
        AssertContained(target);
        target
            .Diagnostics.Should()
            .Contain(d =>
                d.Component == CdcDeploymentComponent.Connect && d.Failure == CdcDeploymentFailure.Timeout
            );
    }

    [TestCase("workflows")]
    [TestCase("source-history")]
    public async Task It_contains_an_existing_incident_despite_missing_other_provenance(string directory)
    {
        _identity = new('b', 64);
        AssertContained(await TargetStatusAsync());
        Directory.Delete(Path.Combine(_root, directory), true);
        _stopped = false;
        var target = await TargetStatusAsync();
        AssertContained(target);
        target.Diagnostics.Should().Contain(d => d.Component == CdcDeploymentComponent.WorkflowState);
    }

    [Test]
    public async Task It_sanitizes_structured_status_output()
    {
        var json = JsonSerializer.Serialize(await RunStatusAsync());
        json.Should()
            .NotContain(_request.Binding.PhysicalSourceFingerprint)
            .And.NotContain("private-source")
            .And.NotContain("super-secret")
            .And.NotContain("DATABASE_PASSWORD")
            .And.NotContain("worker:8083");
    }
}
