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
[Category("CdcRetry")]
public class Given_CdcInitialEnableRetryClassifier
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 8, 17, 13, 9, 55, TimeSpan.Zero);
    private static readonly DateTimeOffset DurableObservedAt = new(2026, 8, 17, 13, 10, 10, TimeSpan.Zero);
    private static readonly DateTimeOffset ObservedAt = DurableObservedAt.AddSeconds(1);
    private static readonly DateTimeOffset Now = ObservedAt.AddMinutes(1);

    private const string ProofId = "proof-1";
    private const string SetupControllerRunId = "setup-run-1";

    [Test]
    public void It_allows_pre_binding_creation_only_for_a_resolved_empty_disabled_source()
    {
        CdcInitialEnablePreBindingEligibilityResult result =
            CdcInitialEnableRetryClassifier.EvaluatePreBindingEligibility(
                PreBindingInput(Eligibility(CdcLifecycleState.Disabled))
            );

        result.CanCreateBinding.Should().BeTrue();
        result.Rejection.Should().BeNull();
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_rejects_pre_binding_when_source_resolution_is_not_proven()
    {
        CdcInitialEnablePreBindingEligibilityResult result =
            CdcInitialEnableRetryClassifier.EvaluatePreBindingEligibility(
                PreBindingInput(Eligibility(CdcLifecycleState.Disabled), physicalSourceFingerprint: null)
            );

        result.CanCreateBinding.Should().BeFalse();
        result.Rejection.Should().NotBeNull();
        result.Rejection!.RetryClassification.Should().Be(CdcRetryClassification.RejectNotInitialWorkflow);
        result.Rejection.Action.Should().Be(CdcRetryAction.RetireUnusedBindingAndReprovision);
        result
            .Rejection.Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.SourceMismatch);
    }

    [Test]
    public void It_classifies_exact_disabled_binding_as_guarded_activation_retry()
    {
        CdcRetry retry = CdcInitialEnableRetryClassifier.EvaluateRetry(
            RetryInput(Eligibility(CdcLifecycleState.Disabled))
        );

        retry.RetryClassification.Should().Be(CdcRetryClassification.RetryGuardedActivation);
        retry.Action.Should().Be(CdcRetryAction.Proceed);
        retry.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.None);
        retry.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_classifies_exact_tracking_binding_as_provider_topic_connector_resume()
    {
        CdcRetry retry = CdcInitialEnableRetryClassifier.EvaluateRetry(
            RetryInput(Eligibility(CdcLifecycleState.Tracking))
        );

        retry.RetryClassification.Should().Be(CdcRetryClassification.ResumeProviderTopicConnectorSetup);
        retry.Action.Should().Be(CdcRetryAction.Proceed);
        retry.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.None);
        retry.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_rejects_exact_binding_retry_when_current_source_resolution_is_unavailable()
    {
        CdcRetry retry = CdcInitialEnableRetryClassifier.EvaluateRetry(
            RetryInput(Eligibility(CdcLifecycleState.Tracking), physicalSourceFingerprint: null)
        );

        retry.RetryClassification.Should().Be(CdcRetryClassification.RejectNotInitialWorkflow);
        retry.Action.Should().Be(CdcRetryAction.RetireUnusedBindingAndReprovision);
        retry.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        retry
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.SourceMismatch);
    }

    [Test]
    public void It_rejects_tracking_without_a_binding()
    {
        CdcRetry retry = CdcInitialEnableRetryClassifier.EvaluateRetry(
            RetryInput(
                Eligibility(CdcLifecycleState.Tracking),
                new(
                    CdcJsonContract.CurrentContractVersion,
                    ObservedAt,
                    CdcBindingState.BindingMissing,
                    null,
                    null
                )
            )
        );

        retry.RetryClassification.Should().Be(CdcRetryClassification.RejectUnboundTracking);
        retry.Action.Should().Be(CdcRetryAction.FailClosed);
        retry.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.BindingMissing);
    }

    [Test]
    public void It_rejects_binding_mismatch()
    {
        CdcRetry retry = CdcInitialEnableRetryClassifier.EvaluateRetry(
            RetryInput(
                Eligibility(CdcLifecycleState.Disabled),
                new(
                    CdcJsonContract.CurrentContractVersion,
                    ObservedAt,
                    CdcBindingState.BindingMismatch,
                    null,
                    null
                )
            )
        );

        retry.RetryClassification.Should().Be(CdcRetryClassification.RejectBindingMismatch);
        retry.Action.Should().Be(CdcRetryAction.FailClosed);
        retry.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.BindingMismatch);
    }

    [TestCase(CdcLifecycleState.Resetting, CdcRetryClassification.RejectResettingLifecycle)]
    [TestCase(CdcLifecycleState.Rebuilding, CdcRetryClassification.RejectRebuildingLifecycle)]
    public void It_rejects_resetting_or_rebuilding_lifecycle(
        CdcLifecycleState lifecycleState,
        CdcRetryClassification expectedClassification
    )
    {
        CdcRetry retry = CdcInitialEnableRetryClassifier.EvaluateRetry(
            RetryInput(Eligibility(lifecycleState))
        );

        retry.RetryClassification.Should().Be(expectedClassification);
        retry.Action.Should().Be(CdcRetryAction.FailClosed);
        retry.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.ProjectionNonOperational);
    }

    [Test]
    public void It_rejects_cache_ahead_latch()
    {
        CdcRetry retry = CdcInitialEnableRetryClassifier.EvaluateRetry(
            RetryInput(
                Eligibility(CdcLifecycleState.Disabled, cacheAheadState: CdcCacheAheadState.RecoveryRequired)
            )
        );

        retry.RetryClassification.Should().Be(CdcRetryClassification.RejectCacheAheadLatch);
        retry.Action.Should().Be(CdcRetryAction.FailClosed);
        retry.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.ProjectionNonOperational);
    }

    [Test]
    public void It_rejects_unexpected_pre_capture_rows()
    {
        CdcRetry retry = CdcInitialEnableRetryClassifier.EvaluateRetry(
            RetryInput(Eligibility(CdcLifecycleState.Disabled, canonicalRowsPresent: true))
        );

        retry.RetryClassification.Should().Be(CdcRetryClassification.RejectUnexpectedRows);
        retry.Action.Should().Be(CdcRetryAction.FailClosed);
        retry.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.ProjectionBacklog);
        retry.Diagnostics.Select(diagnostic => diagnostic.Path).Should().Contain("$.canonicalRowsPresent");
    }

    [Test]
    public void It_rejects_mismatched_workflow_evidence_as_not_initial_workflow()
    {
        InitialCdcProvisioningProof proof = ProvisioningProof() with { SetupControllerRunId = "other-run" };

        CdcRetry retry = CdcInitialEnableRetryClassifier.EvaluateRetry(
            RetryInput(Eligibility(CdcLifecycleState.Disabled), provisioningProof: proof)
        );

        retry.RetryClassification.Should().Be(CdcRetryClassification.RejectNotInitialWorkflow);
        retry.Action.Should().Be(CdcRetryAction.RetireUnusedBindingAndReprovision);
        retry.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        retry
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.OperationMismatch);
    }

    private static CdcInitialEnablePreBindingEligibilityInput PreBindingInput(
        InitialCdcEligibilityObservation eligibilityObservation,
        string? physicalSourceFingerprint = CdcTargetStatusFixture.SourceFingerprint
    ) =>
        new(
            CdcTargetStatusFixture.OperationId,
            ObservedAt,
            Now,
            CdcTargetStatusFixture.CreateBinding().ToTargetIdentity(),
            physicalSourceFingerprint,
            ProvisioningProof(),
            eligibilityObservation
        );

    private static CdcInitialEnableRetryClassificationInput RetryInput(
        InitialCdcEligibilityObservation eligibilityObservation,
        CdcBindingStateContract? bindingState = null,
        InitialCdcProvisioningProof? provisioningProof = null,
        string? physicalSourceFingerprint = CdcTargetStatusFixture.SourceFingerprint
    )
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();
        CdcBindingStateContract effectiveBindingState =
            bindingState
            ?? new(
                CdcJsonContract.CurrentContractVersion,
                ObservedAt,
                CdcBindingState.BindingPresent,
                binding,
                null
            );

        return new(
            CdcTargetStatusFixture.OperationId,
            ObservedAt,
            Now,
            binding.ToTargetIdentity(),
            physicalSourceFingerprint,
            provisioningProof ?? ProvisioningProof(),
            eligibilityObservation,
            effectiveBindingState
        );
    }

    private static InitialCdcProvisioningProof ProvisioningProof() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            ProofId,
            CdcTargetStatusFixture.OperationId,
            CdcTargetStatusFixture.CreateBinding().ToTargetIdentity(),
            CdcProvider.Postgresql,
            SetupControllerRunId,
            CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning,
            CdcWriteAdmissionState.ClosedNeverOpened,
            IssuedAt
        );

    private static InitialCdcEligibilityObservation Eligibility(
        CdcLifecycleState lifecycleState,
        CdcCacheAheadState cacheAheadState = CdcCacheAheadState.Clear,
        bool canonicalRowsPresent = false,
        bool cacheRowsPresent = false,
        bool workRowsPresent = false
    ) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            CdcTargetStatusFixture.OperationId,
            ObservedAt,
            DurableObservedAt,
            CdcTargetStatusFixture.CreateBinding().ToTargetIdentity(),
            CdcProvider.Postgresql,
            CdcTargetStatusFixture.SourceFingerprint,
            SetupControllerRunId,
            ProofId,
            CdcConsistencyScope.SingleProviderTransaction,
            lifecycleState,
            cacheAheadState,
            canonicalRowsPresent,
            cacheRowsPresent,
            workRowsPresent,
            "single transaction snapshot visible",
            []
        );
}
