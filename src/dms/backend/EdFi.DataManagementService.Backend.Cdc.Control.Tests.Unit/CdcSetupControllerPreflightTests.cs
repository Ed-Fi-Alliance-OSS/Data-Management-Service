// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// The status-endpoint preflight. The caught-up evidence the sequence ends on is read from the
/// running DMS, so a deployment that cannot supply it fails immediately — before a binding record or
/// any external artifact exists — with a message naming what is missing, rather than after
/// provisioning everything and timing out on an observation that was never going to arrive.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcSetupControllerPreflight")]
public class Given_CdcSetupControllerStatusEndpointPreflight
{
    [Test]
    public async Task It_fails_before_provisioning_when_the_status_endpoint_is_not_mapped()
    {
        CdcSetupControllerHarness harness = Harness(CdcProjectionStatusReadOutcome.EndpointNotMapped);

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        Step(admission).Category.Should().Be(CdcDiagnosticCategory.StatusObservationUnavailable);

        // The collector's own diagnostic names the setting that leaves the route unmapped, so the
        // failure is actionable rather than a generic unavailability.
        admission
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Message.Contains("DataManagement:DocumentCache:Status:RequiredRole")
            );
        NothingWasProvisioned(harness);
    }

    [TestCase(CdcProjectionStatusReadOutcome.Unauthorized)]
    [TestCase(CdcProjectionStatusReadOutcome.Unavailable)]
    [TestCase(CdcProjectionStatusReadOutcome.MalformedResponse)]
    public async Task It_fails_before_provisioning_when_the_projection_status_cannot_be_read(
        CdcProjectionStatusReadOutcome outcome
    )
    {
        CdcSetupControllerHarness harness = Harness(outcome);

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        Step(admission).Code.Should().Be("enableProjectionStatusUnavailable");
        NothingWasProvisioned(harness);
    }

    [Test]
    public async Task It_preflights_before_reading_the_binding_or_the_instance_database()
    {
        CdcSetupControllerHarness harness = Harness(CdcProjectionStatusReadOutcome.EndpointNotMapped);

        await harness.EnableAsync();

        using var _ = new AssertionScope();
        A.CallTo(() => harness.Bindings.ReadBindingAsync(A<CdcBindingIdentity>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => harness.Probe.ProbeAsync(A<CdcEligibilityProbeRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_continues_when_the_running_dms_answers_its_projection_status()
    {
        CdcSetupControllerHarness harness = new();

        await harness.EnableAsync();

        using var _ = new AssertionScope();

        // The status endpoint is read before the binding exists, and again by the caught-up steps that
        // depend on it.
        A.CallTo(() => harness.Projection.CollectAsync(A<CdcObservationContext>._, A<CancellationToken>._))
            .MustHaveHappened()
            .Then(
                A.CallTo(() =>
                        harness.Bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._)
                    )
                    .MustHaveHappenedOnceExactly()
            );
    }

    /// <summary>
    /// A target the running DMS does not yet report is not a preflight failure: the correlation states
    /// other than unavailable are decided by the caught-up steps, which observe the projector after
    /// the binding and its artifacts exist.
    /// </summary>
    [Test]
    public async Task It_continues_when_the_dms_has_not_yet_reported_the_target()
    {
        CdcSetupControllerHarness harness = new()
        {
            ProjectionStatus = new(
                CdcProjectionStatusReadOutcome.Succeeded,
                new(CdcSetupControllerHarness.Now, []),
                null
            ),
        };

        await harness.EnableAsync();

        A.CallTo(() => harness.Bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    private static CdcSetupControllerHarness Harness(CdcProjectionStatusReadOutcome outcome) =>
        new() { ProjectionStatus = CdcSetupControllerHarness.StatusEndpointFailure(outcome) };

    private static void NothingWasProvisioned(CdcSetupControllerHarness harness)
    {
        A.CallTo(() => harness.Bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => harness.Bindings.ExactMatchBindingAsync(A<CdcBinding>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Activation.ExecuteAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => harness.ProviderSetup.SetupAsync(A<CdcProviderSetupRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Kafka.EnsureConnectOffsetStoreAsync(
                    A<CdcObservationContext>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Kafka.EnsureBindingKafkaPolicyAsync(
                    A<CdcObservationContext>._,
                    A<CdcArtifactInventory>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    private static CdcDiagnostic Step(CdcAdmission admission) =>
        admission
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "enableProjectionStatusUnavailable")
            .Subject;
}
