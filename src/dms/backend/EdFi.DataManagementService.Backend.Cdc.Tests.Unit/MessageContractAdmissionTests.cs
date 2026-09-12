// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixtureSource(nameof(Scenarios))]
[Category("CdcMessageContract")]
[Parallelizable(ParallelScope.Fixtures)]
public class Given_MessageContractAdmission(CdcProvider provider, string scenario)
{
    private CdcAdmission _baseline = null!;
    private CdcAdmission _actual = null!;
    private CdcComponent _changedStep = null!;

    public static IEnumerable<TestFixtureData> Scenarios()
    {
        string[] cases =
        [
            "valid",
            "first-missing",
            "first-old-operation",
            "first-wrong-source",
            "first-wrong-provider",
            "first-nonoperational",
            "first-backlog",
            "barrier-missing",
            "barrier-before-projection",
            "barrier-wrong-projection",
            "barrier-offset-before-capture",
            "history-missing",
            "history-before-barrier",
            "history-at-barrier",
            "history-unhealthy",
            "second-missing",
            "second-before-history",
            "second-at-history",
            "second-old-operation",
            "second-wrong-source",
            "second-wrong-target",
            "second-backlog",
            "second-nonoperational",
            "second-future",
            "lag-excessive",
            "lag-unknown",
            "lag-old-operation",
            "lag-wrong-source",
            "lag-wrong-target",
            "lag-wrong-provider",
            "lag-future",
            "failed-task",
            "elapsed-time-only",
        ];
        foreach (CdcProvider provider in new[] { CdcProvider.Postgresql, CdcProvider.SqlServer })
        {
            foreach (string scenario in cases)
            {
                TestFixtureData data = new(provider, scenario);
                data.Properties.Set("ScenarioId", $"MC-ADMISSION-{provider}-{scenario}");
                yield return data;
            }
        }
    }

    [SetUp]
    public void Setup()
    {
        using MessageContractAdmissionFixture fixture = new(provider);
        CdcInitialAdmissionEvaluationInput input = fixture.ValidInput();
        _baseline = CdcInitialAdmissionEvaluator.Evaluate(input);
        CdcInitialAdmissionEvaluationInput changed = scenario switch
        {
            "valid" => input,
            "first-missing" => input with { FirstProjectionCaughtUp = null },
            "first-old-operation" => input with
            {
                FirstProjectionCaughtUp = input.FirstProjectionCaughtUp! with
                {
                    OperationId = "previous-operation",
                },
            },
            "first-wrong-source" => input with
            {
                FirstProjectionCaughtUp = input.FirstProjectionCaughtUp! with
                {
                    PhysicalSourceFingerprint = MessageContractAdmissionFixture.OtherFingerprint,
                },
            },
            "first-wrong-provider" => input with
            {
                FirstProjectionCaughtUp = input.FirstProjectionCaughtUp! with
                {
                    Provider =
                        provider == CdcProvider.Postgresql ? CdcProvider.SqlServer : CdcProvider.Postgresql,
                },
            },
            "first-nonoperational" => input with
            {
                FirstProjectionCaughtUp = input.FirstProjectionCaughtUp! with
                {
                    OperationalHealthStatus = DocumentCacheOperationalHealthStatus.NonOperational,
                },
            },
            "first-backlog" => input with
            {
                FirstProjectionCaughtUp = input.FirstProjectionCaughtUp! with
                {
                    CaughtUpStatus = DocumentCacheCaughtUpStatus.NotCaughtUp,
                },
            },
            "barrier-missing" => input with { ProviderBarrier = null },
            "barrier-before-projection" => input with
            {
                ProviderBarrier = input.ProviderBarrier! with
                {
                    BarrierCapturedAt = MessageContractAdmissionFixture.FirstAt.AddTicks(-1),
                },
            },
            "barrier-wrong-projection" => input with
            {
                ProviderBarrier = input.ProviderBarrier! with
                {
                    ProjectionCaughtUpObservedAt = MessageContractAdmissionFixture.FirstAt.AddTicks(-1),
                },
            },
            "barrier-offset-before-capture" => input with
            {
                ProviderBarrier = input.ProviderBarrier! with
                {
                    ConnectorOffsetObservedAt = MessageContractAdmissionFixture.CaptureAt.AddTicks(-1),
                },
            },
            "history-missing" => input with { SourceHistory = null },
            "history-before-barrier" => input with
            {
                SourceHistory = input.SourceHistory! with
                {
                    ObservedAt = MessageContractAdmissionFixture.BarrierAt.AddTicks(-1),
                },
            },
            "history-at-barrier" => input with
            {
                SourceHistory = input.SourceHistory! with
                {
                    ObservedAt = MessageContractAdmissionFixture.BarrierAt,
                },
            },
            "history-unhealthy" => input with
            {
                SourceHistory = input.SourceHistory! with
                {
                    Continuity = CdcSourceHistoryContinuity.Unknown,
                    ProviderArtifactState = CdcProviderArtifactContinuityState.Unknown,
                    RetainedRangeState = CdcProviderRetainedRangeState.Unknown,
                    PositionEvidence = null,
                },
            },
            "second-missing" => input with { SecondProjectionCaughtUp = null },
            "second-before-history" => input with
            {
                SecondProjectionCaughtUp = input.SecondProjectionCaughtUp! with
                {
                    ProjectionObservedAt = MessageContractAdmissionFixture.HistoryAt.AddTicks(-1),
                },
            },
            "second-at-history" => input with
            {
                SecondProjectionCaughtUp = input.SecondProjectionCaughtUp! with
                {
                    ProjectionObservedAt = MessageContractAdmissionFixture.HistoryAt,
                },
            },
            "second-old-operation" => input with
            {
                SecondProjectionCaughtUp = input.SecondProjectionCaughtUp! with
                {
                    OperationId = "previous-operation",
                },
            },
            "second-wrong-source" => input with
            {
                SecondProjectionCaughtUp = input.SecondProjectionCaughtUp! with
                {
                    PhysicalSourceFingerprint = MessageContractAdmissionFixture.OtherFingerprint,
                },
            },
            "second-wrong-target" => input with
            {
                SecondProjectionCaughtUp = input.SecondProjectionCaughtUp! with
                {
                    TargetIdentity = input.TargetIdentity with { DataStoreId = "other-store" },
                },
            },
            "second-backlog" => input with
            {
                SecondProjectionCaughtUp = input.SecondProjectionCaughtUp! with
                {
                    CaughtUpStatus = DocumentCacheCaughtUpStatus.NotCaughtUp,
                },
            },
            "second-nonoperational" => input with
            {
                SecondProjectionCaughtUp = input.SecondProjectionCaughtUp! with
                {
                    OperationalHealthStatus = DocumentCacheOperationalHealthStatus.NonOperational,
                },
            },
            "second-future" => input with
            {
                SecondProjectionCaughtUp = input.SecondProjectionCaughtUp! with
                {
                    ObservedAt = MessageContractAdmissionFixture.Now.AddTicks(1),
                    ProjectionObservedAt = MessageContractAdmissionFixture.Now.AddTicks(1),
                },
            },
            "lag-excessive" => input with
            {
                Lag = input.Lag! with
                {
                    LagState = CdcConnectorLagState.Exceeded,
                    CurrentLagMilliseconds = 1001,
                },
            },
            "lag-unknown" => input with { Lag = null },
            "lag-old-operation" => input with { Lag = input.Lag! with { OperationId = "old-operation" } },
            "lag-wrong-source" => input with
            {
                Lag = input.Lag! with
                {
                    PhysicalSourceFingerprint = MessageContractAdmissionFixture.OtherFingerprint,
                },
            },
            "lag-wrong-target" => input with
            {
                Lag = input.Lag! with
                {
                    TargetIdentity = input.TargetIdentity with { DataStoreId = "other-store" },
                },
            },
            "lag-wrong-provider" => input with
            {
                Lag = input.Lag! with
                {
                    Provider =
                        provider == CdcProvider.Postgresql ? CdcProvider.SqlServer : CdcProvider.Postgresql,
                },
            },
            "lag-future" => input with { Lag = input.Lag! with { ObservedAt = input.NowUtc.AddTicks(1) } },
            "failed-task" => input with { ConnectorRuntime = fixture.Runtime("FAILED") },
            "elapsed-time-only" => input with
            {
                ProviderBarrier = null,
                NowUtc = input.NowUtc.AddHours(1),
                ObservedAt = input.ObservedAt.AddHours(1),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        _actual = CdcInitialAdmissionEvaluator.Evaluate(changed);
        _changedStep = scenario.Split('-')[0] switch
        {
            "first" => _actual.Steps.FirstProjectionCaughtUp,
            "history" => _actual.Steps.SourceHistory,
            "second" => _actual.Steps.SecondProjectionCaughtUp,
            "lag" => _actual.Steps.Lag,
            "failed" => _actual.Steps.ConnectorAndTopicValidation,
            _ => _actual.Steps.ProviderBarrier,
        };
    }

    [Test]
    public void It_starts_from_admitted_evaluator_inputs_with_synthetic_prerequisites() =>
        _baseline
            .AdmissionState.Should()
            .Be(
                CdcAdmissionState.Admitted,
                "baseline diagnostics: {0}",
                string.Join(", ", _baseline.Diagnostics.Select(d => $"{d.Category}:{d.Path}"))
            );

    [Test]
    public void It_requires_every_admission_condition()
    {
        if (scenario == "valid")
        {
            _actual.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
        }
        else
        {
            _actual.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        }
    }

    [Test]
    public void It_blocks_the_changed_step()
    {
        if (scenario == "valid")
        {
            _changedStep.State.Should().Be(CdcComponentState.Satisfied);
        }
        else
        {
            _changedStep.State.Should().NotBe(CdcComponentState.Satisfied);
            if (scenario == "failed-task")
            {
                _changedStep.Category.Should().Be(CdcBlockingCategory.ConnectorNotRunning);
            }
            if (scenario == "lag-excessive")
            {
                _changedStep.Category.Should().Be(CdcBlockingCategory.LagExceeded);
            }
        }
    }

    [Test]
    public void It_preserves_fixed_provisioning_prerequisites()
    {
        _actual.Steps.Binding.State.Should().Be(CdcComponentState.Satisfied);
        _actual.Steps.GuardedTrackingActivation.State.Should().Be(CdcComponentState.Satisfied);
        _actual.Steps.ProviderSetup.State.Should().Be(CdcComponentState.Satisfied);
    }
}
