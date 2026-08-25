// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcAdmission")]
public class Given_CdcAdmissionEvaluator
{
    [Test]
    public void It_admits_when_same_operation_evidence_is_satisfied_in_order()
    {
        CdcAdmission admission = CdcInitialAdmissionEvaluator.Evaluate(CdcAdmissionFixture.ValidInput());

        admission.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
        admission.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.None);
        admission.Steps.Binding.State.Should().Be(CdcComponentState.Satisfied);
        admission.Steps.GuardedTrackingActivation.State.Should().Be(CdcComponentState.Satisfied);
        admission.Steps.ProviderSetup.State.Should().Be(CdcComponentState.Satisfied);
        admission.Steps.ConnectorAndTopicValidation.State.Should().Be(CdcComponentState.Satisfied);
        admission.Steps.FirstProjectionCaughtUp.State.Should().Be(CdcComponentState.Satisfied);
        admission.Steps.ProviderBarrier.State.Should().Be(CdcComponentState.Satisfied);
        admission.Steps.SourceHistory.State.Should().Be(CdcComponentState.Satisfied);
        admission.Steps.SecondProjectionCaughtUp.State.Should().Be(CdcComponentState.Satisfied);
        admission.Steps.Lag.State.Should().Be(CdcComponentState.Satisfied);
        admission.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_does_not_admit_when_current_source_resolution_is_unavailable()
    {
        CdcAdmission admission = CdcInitialAdmissionEvaluator.Evaluate(
            CdcAdmissionFixture.ValidInput() with
            {
                PhysicalSourceFingerprint = null,
            }
        );

        admission.AdmissionState.Should().Be(CdcAdmissionState.Unknown);
        admission.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        admission.Steps.Binding.State.Should().Be(CdcComponentState.Unknown);
        admission.Steps.Binding.Category.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        admission.Steps.GuardedTrackingActivation.State.Should().Be(CdcComponentState.Unknown);
        admission
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.StatusObservationUnavailable);
    }

    [Test]
    public void It_does_not_admit_when_current_state_store_diagnostics_exist_with_a_valid_binding_state()
    {
        CdcAdmission admission = CdcInitialAdmissionEvaluator.Evaluate(
            CdcAdmissionFixture.ValidInput() with
            {
                StateStoreDiagnostics =
                [
                    new(
                        CdcDiagnosticCategory.LocalStateUnavailable,
                        "$.bindingState",
                        "local state unavailable"
                    ),
                ],
            }
        );

        admission.AdmissionState.Should().Be(CdcAdmissionState.Unknown);
        admission.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        admission.Steps.Binding.State.Should().Be(CdcComponentState.Unknown);
        admission.Steps.Binding.Category.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        admission
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.LocalStateUnavailable
                && diagnostic.Path == "$.bindingState"
            );
        admission
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .NotContain(CdcDiagnosticCategory.BindingIdentityMismatch);
    }

    [Test]
    public void It_keeps_explicit_current_source_mismatch_as_source_mismatch()
    {
        CdcAdmission admission = CdcInitialAdmissionEvaluator.Evaluate(
            CdcAdmissionFixture.ValidInput() with
            {
                PhysicalSourceFingerprint = CdcTargetStatusFixture.OtherSourceFingerprint,
            }
        );

        admission.AdmissionState.Should().Be(CdcAdmissionState.NotAdmitted);
        admission.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.SourceMismatch);
        admission.Steps.Binding.State.Should().Be(CdcComponentState.NotSatisfied);
        admission.Steps.Binding.Category.Should().Be(CdcBlockingCategory.SourceMismatch);
    }

    [TestCase(null, CdcDiagnosticCategory.MissingRequiredField)]
    [TestCase("not-a-sha256-fingerprint", CdcDiagnosticCategory.MalformedPayload)]
    public void It_maps_missing_or_malformed_observation_source_fingerprint_to_unavailable_admission_evidence(
        string? observationFingerprint,
        CdcDiagnosticCategory expectedDiagnosticCategory
    )
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();

        CdcAdmission admission = CdcInitialAdmissionEvaluator.Evaluate(
            CdcAdmissionFixture.ValidInput(binding) with
            {
                SecondProjectionCaughtUp = CdcAdmissionFixture.SecondProjection(binding) with
                {
                    PhysicalSourceFingerprint = observationFingerprint,
                },
            }
        );

        admission.AdmissionState.Should().Be(CdcAdmissionState.Unknown);
        admission.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        admission.Steps.SecondProjectionCaughtUp.State.Should().Be(CdcComponentState.Unknown);
        admission
            .Steps.SecondProjectionCaughtUp.Category.Should()
            .Be(CdcBlockingCategory.StatusObservationUnavailable);
        admission
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(expectedDiagnosticCategory)
            .And.NotContain(CdcDiagnosticCategory.SourceMismatch);
    }

    [Test]
    public void It_keeps_provider_barrier_not_reached_as_the_primary_admission_blocker()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();

        CdcAdmission admission = CdcInitialAdmissionEvaluator.Evaluate(
            CdcAdmissionFixture.ValidInput(binding) with
            {
                ProviderBarrier = CdcTargetStatusFixture.ProviderBarrier(binding) with
                {
                    BarrierState = CdcProviderBarrierState.NotReached,
                    CommittedPosition = null,
                },
            }
        );

        admission.AdmissionState.Should().Be(CdcAdmissionState.NotAdmitted);
        admission.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.ProviderBarrierNotReached);
        admission.Steps.ProviderBarrier.State.Should().Be(CdcComponentState.NotSatisfied);
        admission.Steps.ProviderBarrier.Category.Should().Be(CdcBlockingCategory.ProviderBarrierNotReached);
        admission.Steps.Lag.State.Should().Be(CdcComponentState.Satisfied);
    }

    [Test]
    public void It_does_not_admit_when_guarded_tracking_activation_is_still_pending()
    {
        CdcAdmission admission = CdcInitialAdmissionEvaluator.Evaluate(
            CdcAdmissionFixture.ValidInput(lifecycleState: CdcLifecycleState.Disabled)
        );

        admission.AdmissionState.Should().Be(CdcAdmissionState.NotAdmitted);
        admission.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.ProjectionNonOperational);
        admission.Steps.GuardedTrackingActivation.State.Should().Be(CdcComponentState.NotSatisfied);
        admission
            .Steps.GuardedTrackingActivation.Category.Should()
            .Be(CdcBlockingCategory.ProjectionNonOperational);
    }
}

[TestFixture]
[Parallelizable]
[Category("CdcAdmissionOrdering")]
public class Given_CdcAdmissionOrdering
{
    [Test]
    public void It_admits_when_source_history_is_between_provider_barrier_success_and_second_projection()
    {
        CdcAdmission admission = CdcInitialAdmissionEvaluator.Evaluate(CdcAdmissionFixture.ValidInput());

        admission.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
        admission.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.None);
        admission.Steps.SourceHistory.State.Should().Be(CdcComponentState.Satisfied);
        admission.Steps.SecondProjectionCaughtUp.State.Should().Be(CdcComponentState.Satisfied);
        admission.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_requires_source_history_observation_after_provider_barrier_success()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();
        DateTimeOffset staleSourceHistoryTime = CdcTargetStatusFixture.ObservationObservedAt.AddMilliseconds(
            -500
        );

        CdcAdmission admission = CdcInitialAdmissionEvaluator.Evaluate(
            CdcAdmissionFixture.ValidInput(binding) with
            {
                SourceHistory = CdcTargetStatusFixture.SourceHistory(binding) with
                {
                    ObservedAt = staleSourceHistoryTime,
                },
            }
        );

        admission.AdmissionState.Should().Be(CdcAdmissionState.Unknown);
        admission.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        admission.Steps.SourceHistory.State.Should().Be(CdcComponentState.Unknown);
        admission.Steps.SourceHistory.Category.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        admission
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.InvalidOrdering
                && diagnostic.Path == "$.sourceHistory.observedAt"
            );
    }

    [Test]
    public void It_requires_second_projection_caught_up_observation_after_source_history()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();
        DateTimeOffset sourceHistoryAfterSecondProjection =
            CdcTargetStatusFixture.ObservationObservedAt.AddSeconds(3);

        CdcAdmission admission = CdcInitialAdmissionEvaluator.Evaluate(
            CdcAdmissionFixture.ValidInput(binding) with
            {
                SourceHistory = CdcTargetStatusFixture.SourceHistory(binding) with
                {
                    ObservedAt = sourceHistoryAfterSecondProjection,
                },
            }
        );

        admission.AdmissionState.Should().Be(CdcAdmissionState.Unknown);
        admission.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        admission.Steps.SourceHistory.State.Should().Be(CdcComponentState.Satisfied);
        admission.Steps.SecondProjectionCaughtUp.State.Should().Be(CdcComponentState.Unknown);
        admission
            .Steps.SecondProjectionCaughtUp.Category.Should()
            .Be(CdcBlockingCategory.StatusObservationUnavailable);
        admission
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.InvalidOrdering
                && diagnostic.Path == "$.sourceHistory.observedAt"
            );
    }

    [Test]
    public void It_requires_the_second_projection_caught_up_observation_after_provider_barrier_success()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();
        DateTimeOffset outOfOrderProjectionTime =
            CdcTargetStatusFixture.ObservationObservedAt.AddMilliseconds(-500);

        CdcAdmission admission = CdcInitialAdmissionEvaluator.Evaluate(
            CdcAdmissionFixture.ValidInput(binding) with
            {
                SecondProjectionCaughtUp = CdcAdmissionFixture.SecondProjection(binding) with
                {
                    ObservedAt = outOfOrderProjectionTime,
                    ProjectionObservedAt = outOfOrderProjectionTime,
                },
            }
        );

        admission.AdmissionState.Should().Be(CdcAdmissionState.Unknown);
        admission.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        admission.Steps.SecondProjectionCaughtUp.State.Should().Be(CdcComponentState.Unknown);
        admission
            .Steps.SecondProjectionCaughtUp.Category.Should()
            .Be(CdcBlockingCategory.StatusObservationUnavailable);
        admission
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidOrdering);
    }
}

internal static class CdcAdmissionFixture
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 8, 17, 13, 9, 55, TimeSpan.Zero);
    private static readonly DateTimeOffset EligibilityDurableObservedAt = new(
        2026,
        8,
        17,
        13,
        10,
        5,
        TimeSpan.Zero
    );
    private static readonly DateTimeOffset EligibilityObservedAt = EligibilityDurableObservedAt.AddSeconds(1);
    private static readonly DateTimeOffset Now = CdcTargetStatusFixture.StatusObservedAt.AddMinutes(1);

    private const string ProofId = "proof-1";
    private const string SetupControllerRunId = "setup-run-1";

    public static CdcInitialAdmissionEvaluationInput ValidInput(
        CdcBinding? binding = null,
        CdcLifecycleState lifecycleState = CdcLifecycleState.Tracking
    )
    {
        CdcBinding effectiveBinding = binding ?? CdcTargetStatusFixture.CreateBinding();

        return new(
            CdcTargetStatusFixture.OperationId,
            CdcTargetStatusFixture.StatusObservedAt,
            Now,
            effectiveBinding.ToTargetIdentity(),
            CdcTargetStatusFixture.SourceFingerprint,
            ProvisioningProof(effectiveBinding),
            Eligibility(effectiveBinding, lifecycleState),
            new(
                CdcJsonContract.CurrentContractVersion,
                CdcTargetStatusFixture.ObservationObservedAt,
                CdcBindingState.BindingPresent,
                effectiveBinding,
                null
            )
        )
        {
            ProviderSetup = CdcTargetStatusFixture.ProviderSetup(effectiveBinding),
            KafkaPolicy = CdcTargetStatusFixture.KafkaPolicy(effectiveBinding),
            ConnectOffsetStore = CdcTargetStatusFixture.ConnectOffsetStore(effectiveBinding),
            ConnectorConfig = CdcTargetStatusFixture.ConnectorConfig(effectiveBinding),
            ConnectorRuntime = CdcTargetStatusFixture.ConnectorRuntime(effectiveBinding),
            FirstProjectionCaughtUp = FirstProjection(effectiveBinding),
            ProviderBarrier = CdcTargetStatusFixture.ProviderBarrier(effectiveBinding),
            SourceHistory = SourceHistory(effectiveBinding),
            SecondProjectionCaughtUp = SecondProjection(effectiveBinding),
            Lag = CdcTargetStatusFixture.Lag(effectiveBinding),
        };
    }

    public static CdcSourceHistoryObservation SourceHistory(CdcBinding binding)
    {
        DateTimeOffset observedAt = CdcTargetStatusFixture.ObservationObservedAt.AddSeconds(1);

        return CdcTargetStatusFixture.SourceHistory(binding) with
        {
            ObservedAt = observedAt,
        };
    }

    public static CdcProjectionCorrelationObservation SecondProjection(CdcBinding binding)
    {
        DateTimeOffset projectionObservedAt = CdcTargetStatusFixture.ObservationObservedAt.AddSeconds(2);

        return CdcTargetStatusFixture.Projection(binding) with
        {
            ObservedAt = projectionObservedAt,
            ProjectionObservedAt = projectionObservedAt,
        };
    }

    private static CdcProjectionCorrelationObservation FirstProjection(CdcBinding binding)
    {
        DateTimeOffset projectionObservedAt = CdcTargetStatusFixture.ObservationObservedAt.AddSeconds(-3);

        return CdcTargetStatusFixture.Projection(binding) with
        {
            ObservedAt = projectionObservedAt,
            ProjectionObservedAt = projectionObservedAt,
        };
    }

    private static InitialCdcProvisioningProof ProvisioningProof(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            ProofId,
            CdcTargetStatusFixture.OperationId,
            binding.ToTargetIdentity(),
            binding.Provider,
            SetupControllerRunId,
            CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning,
            CdcWriteAdmissionState.ClosedNeverOpened,
            IssuedAt
        );

    private static InitialCdcEligibilityObservation Eligibility(
        CdcBinding binding,
        CdcLifecycleState lifecycleState
    ) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            CdcTargetStatusFixture.OperationId,
            EligibilityObservedAt,
            EligibilityDurableObservedAt,
            binding.ToTargetIdentity(),
            binding.Provider,
            CdcTargetStatusFixture.SourceFingerprint,
            SetupControllerRunId,
            ProofId,
            CdcConsistencyScope.SingleProviderTransaction,
            lifecycleState,
            CdcCacheAheadState.Clear,
            false,
            false,
            false,
            "single transaction snapshot visible",
            []
        );
}
