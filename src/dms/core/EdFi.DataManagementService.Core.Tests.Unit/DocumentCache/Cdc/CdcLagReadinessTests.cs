// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture("within threshold", CdcReadiness.Ready, CdcAdmissionState.Admitted)]
[TestFixture("unknown lag", CdcReadiness.Unknown, CdcAdmissionState.Unknown)]
[TestFixture("exceeded lag", CdcReadiness.NotReady, CdcAdmissionState.NotAdmitted)]
[TestFixture("barrier not reached", CdcReadiness.NotReady, CdcAdmissionState.NotAdmitted)]
[Category("CdcConnectorLagObservation")]
public class Given_CdcReadinessWithoutLagStatistics(
    string scenario,
    CdcReadiness expectedReadiness,
    CdcAdmissionState expectedAdmission
)
{
    private CdcTargetStatus _status = null!;
    private CdcAdmission _admission = null!;

    [SetUp]
    public void Setup()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();
        CdcConnectorLagObservation lag = CdcTargetStatusFixture.Lag(binding) with
        {
            P50LagMilliseconds = null,
            P95LagMilliseconds = null,
            P99LagMilliseconds = null,
        };
        lag = scenario switch
        {
            "unknown lag" => lag with
            {
                LagState = CdcConnectorLagState.Unknown,
                CurrentLagMilliseconds = null,
            },
            "exceeded lag" => lag with
            {
                LagState = CdcConnectorLagState.Exceeded,
                CurrentLagMilliseconds = 2_000,
            },
            _ => lag,
        };
        CdcProviderBarrierObservation barrier = CdcTargetStatusFixture.ProviderBarrier(binding);
        if (scenario == "barrier not reached")
        {
            barrier = barrier with
            {
                BarrierState = CdcProviderBarrierState.NotReached,
                CommittedPosition = null,
            };
        }

        _status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding) with
            {
                Lag = lag,
                ProviderBarrier = barrier,
            }
        );
        _admission = CdcInitialAdmissionEvaluator.Evaluate(
            CdcAdmissionFixture.ValidInput(binding) with
            {
                Lag = lag,
                ProviderBarrier = barrier,
            }
        );
    }

    [Test]
    public void It_requires_current_lag_and_the_provider_barrier_for_readiness() =>
        _status.Readiness.Should().Be(expectedReadiness);

    [Test]
    public void It_requires_current_lag_and_the_provider_barrier_for_initial_admission() =>
        _admission.AdmissionState.Should().Be(expectedAdmission);
}
