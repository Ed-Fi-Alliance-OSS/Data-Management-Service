// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache;

[TestFixture]
[Parallelizable]
[Category("DocumentCachePreflightClassifier")]
public class DocumentCachePreflightClassifierTests
{
    private static readonly DateTimeOffset _observedAt = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly DocumentCacheTargetKey _targetKey = DocumentCacheTargetKey.Create("TenantA", 7);

    private static readonly DocumentCacheAdministrativeTargetKey _administrativeTargetKey =
        DocumentCacheAdministrativeTargetKey.FromTargetKey(_targetKey);

    private static readonly DocumentCacheTargetContextGeneration _generation = new(3);

    private static readonly DocumentCachePhysicalSourceFingerprint _fingerprint = new(
        "sha256:0123456789abcdef"
    );

    private static readonly DocumentCachePhysicalSourceFingerprint _otherFingerprint = new(
        "sha256:fedcba9876543210"
    );

    private static readonly DocumentCacheTargetEffectiveSettings _settings = new(
        readAccelerationEnabled: true,
        directFillTimeout: TimeSpan.FromMilliseconds(250),
        projectorPollInterval: TimeSpan.FromSeconds(5),
        projectorPageSize: 100,
        projectorMaxConcurrentTargets: 2,
        projectorFailureBackoff: TimeSpan.FromSeconds(30),
        projectorBaselineHighWaterMark: 1000
    );

    private static readonly DocumentCacheInventoryValidationResult _satisfiedInventory = new(
        DocumentCacheInventoryStatus.Satisfied,
        "Inventory satisfied."
    );

    private static readonly DocumentCacheEnqueueTriggerValidationResult _satisfiedEnqueueTrigger = new(
        DocumentCacheEnqueueTriggerStatus.Satisfied,
        "Enqueue trigger satisfied."
    );

    [TestFixture]
    [Parallelizable]
    public class Given_Guarded_New_Empty_Activation : DocumentCachePreflightClassifierTests
    {
        [Test]
        public void It_should_classify_disabled_clear_empty_targets_as_eligible()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    GuardedFacts()
                );

            result.Classification.Should().Be(DocumentCacheAdministrativePreflightClassification.Eligible);
            result.Command.Should().Be(DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation);
            result.ObservedLifecycle.Should().Be(DocumentCacheLifecycleState.Disabled);
            result.CacheAheadRecoveryRequired.Should().BeFalse();
            result.PhysicalSourceFingerprint.Should().Be(_fingerprint);
            result.TargetContextGeneration.Should().Be(_generation.Value);
            result.NoMutationGuarantee.Should().BeNull();
        }

        [TestCase(DocumentCacheLifecycleState.Tracking)]
        [TestCase(DocumentCacheLifecycleState.Rebuilding)]
        public void It_should_reject_lifecycle_mismatches(DocumentCacheLifecycleState lifecycleState)
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    EligibleObservation(lifecycleState),
                    GuardedFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.LifecycleMismatch,
                DocumentCacheTargetDiagnosticCategory.LifecycleMismatch,
                lifecycleState
            );
        }

        [Test]
        public void It_should_reject_resetting_as_explicit_operator_recovery()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Resetting),
                    GuardedFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.ResettingRequiresExplicitOperatorRecovery,
                DocumentCacheTargetDiagnosticCategory.ResettingRequiresExplicitOperatorRecovery,
                DocumentCacheLifecycleState.Resetting
            );
        }

        [Test]
        public void It_should_reject_a_set_cache_ahead_latch()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    EligibleObservation(
                        DocumentCacheLifecycleState.Disabled,
                        cacheAheadRecoveryRequired: true
                    ),
                    GuardedFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.CacheAheadLatchSet,
                DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet
            );
        }

        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        public void It_should_reject_nonempty_guarded_activation_state(
            bool canonicalDocumentsEmpty,
            bool documentCacheEmpty,
            bool documentProjectionWorkEmpty
        )
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    GuardedFacts(
                        guardedNewEmptyState: new DocumentCacheGuardedNewEmptyActivationState(
                            canonicalDocumentsEmpty,
                            documentCacheEmpty,
                            documentProjectionWorkEmpty,
                            "Guarded activation state was not empty."
                        )
                    )
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.NonemptyGuardedActivationState,
                DocumentCacheTargetDiagnosticCategory.NonemptyGuardedActivationState
            );
        }

        [Test]
        public void It_should_reject_command_time_provider_prerequisite_failure()
        {
            DocumentCacheTargetObservation startupEligibleObservation = EligibleObservation(
                DocumentCacheLifecycleState.Disabled,
                sqlServerPrerequisites: SatisfiedActivationPrerequisites().SqlServerPrerequisites
            );

            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    startupEligibleObservation,
                    GuardedFacts(activationProviderPrerequisites: FailedActivationPrerequisites())
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.ProviderPrerequisiteFailed,
                DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed
            );
        }

        [TestCase(
            DocumentCacheLifecycleState.Tracking,
            DocumentCacheAdministrativePreflightClassification.LifecycleMismatch,
            DocumentCacheTargetDiagnosticCategory.LifecycleMismatch
        )]
        [TestCase(
            DocumentCacheLifecycleState.Resetting,
            DocumentCacheAdministrativePreflightClassification.ResettingRequiresExplicitOperatorRecovery,
            DocumentCacheTargetDiagnosticCategory.ResettingRequiresExplicitOperatorRecovery
        )]
        public void It_should_reject_lifecycle_state_before_failed_command_time_provider_prerequisites(
            DocumentCacheLifecycleState lifecycleState,
            DocumentCacheAdministrativePreflightClassification expectedClassification,
            DocumentCacheTargetDiagnosticCategory expectedDiagnosticCategory
        )
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    EligibleObservation(lifecycleState),
                    GuardedFacts(activationProviderPrerequisites: FailedActivationPrerequisites())
                );

            AssertRejected(result, expectedClassification, expectedDiagnosticCategory, lifecycleState);
        }

        [Test]
        public void It_should_reject_cache_ahead_latch_before_failed_command_time_provider_prerequisites()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    EligibleObservation(
                        DocumentCacheLifecycleState.Disabled,
                        cacheAheadRecoveryRequired: true
                    ),
                    GuardedFacts(activationProviderPrerequisites: FailedActivationPrerequisites())
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.CacheAheadLatchSet,
                DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet
            );
        }

        [Test]
        public void It_should_reject_expected_source_mismatch()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(expectedPhysicalSourceFingerprint: _otherFingerprint),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    GuardedFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.ExpectedSourceMismatch,
                DocumentCacheTargetDiagnosticCategory.ExpectedSourceMismatch
            );
        }

        [Test]
        public void It_should_reject_expected_source_mismatch_before_failed_command_time_provider_prerequisites()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(expectedPhysicalSourceFingerprint: _otherFingerprint),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    GuardedFacts(activationProviderPrerequisites: FailedActivationPrerequisites())
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.ExpectedSourceMismatch,
                DocumentCacheTargetDiagnosticCategory.ExpectedSourceMismatch
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Offline_Read_Acceleration_Activation : DocumentCachePreflightClassifierTests
    {
        [Test]
        public void It_should_classify_disabled_internal_only_targets_as_eligible()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineReadAccelerationActivation(
                    OfflineActivationRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    OfflineActivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                    )
                );

            result.Classification.Should().Be(DocumentCacheAdministrativePreflightClassification.Eligible);
            result.Command.Should().Be(DocumentCacheAdministrativeCommand.OfflineReadAccelerationActivation);
            result
                .DownstreamPublicationStatus.Should()
                .Be(DocumentCacheDownstreamPublicationStatus.InternalOnly);
            result.ObservedLifecycle.Should().Be(DocumentCacheLifecycleState.Disabled);
        }

        [Test]
        public void It_should_reject_cache_ahead_latch_before_offline_activation()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineReadAccelerationActivation(
                    OfflineActivationRequest(),
                    EligibleObservation(
                        DocumentCacheLifecycleState.Disabled,
                        cacheAheadRecoveryRequired: true
                    ),
                    OfflineActivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                    )
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.CacheAheadLatchSet,
                DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet
            );
        }

        [Test]
        public void It_should_reject_command_time_provider_prerequisite_failure()
        {
            DocumentCacheTargetObservation startupEligibleObservation = EligibleObservation(
                DocumentCacheLifecycleState.Disabled,
                sqlServerPrerequisites: SatisfiedActivationPrerequisites().SqlServerPrerequisites
            );

            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineReadAccelerationActivation(
                    OfflineActivationRequest(),
                    startupEligibleObservation,
                    OfflineActivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly),
                        activationProviderPrerequisites: FailedActivationPrerequisites()
                    )
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.ProviderPrerequisiteFailed,
                DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed
            );
        }

        [TestCase(
            DocumentCacheLifecycleState.Tracking,
            DocumentCacheAdministrativePreflightClassification.LifecycleMismatch,
            DocumentCacheTargetDiagnosticCategory.LifecycleMismatch
        )]
        [TestCase(
            DocumentCacheLifecycleState.Resetting,
            DocumentCacheAdministrativePreflightClassification.ResettingRequiresExplicitOperatorRecovery,
            DocumentCacheTargetDiagnosticCategory.ResettingRequiresExplicitOperatorRecovery
        )]
        public void It_should_reject_lifecycle_state_before_failed_command_time_provider_prerequisites(
            DocumentCacheLifecycleState lifecycleState,
            DocumentCacheAdministrativePreflightClassification expectedClassification,
            DocumentCacheTargetDiagnosticCategory expectedDiagnosticCategory
        )
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineReadAccelerationActivation(
                    OfflineActivationRequest(),
                    EligibleObservation(lifecycleState),
                    OfflineActivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly),
                        activationProviderPrerequisites: FailedActivationPrerequisites()
                    )
                );

            AssertRejected(result, expectedClassification, expectedDiagnosticCategory, lifecycleState);
        }

        [Test]
        public void It_should_reject_cache_ahead_latch_before_failed_command_time_provider_prerequisites()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineReadAccelerationActivation(
                    OfflineActivationRequest(),
                    EligibleObservation(
                        DocumentCacheLifecycleState.Disabled,
                        cacheAheadRecoveryRequired: true
                    ),
                    OfflineActivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly),
                        activationProviderPrerequisites: FailedActivationPrerequisites()
                    )
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.CacheAheadLatchSet,
                DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet
            );
        }

        [Test]
        public void It_should_reject_expected_source_mismatch_before_failed_command_time_provider_prerequisites()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineReadAccelerationActivation(
                    OfflineActivationRequest(expectedPhysicalSourceFingerprint: _otherFingerprint),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    OfflineActivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly),
                        activationProviderPrerequisites: FailedActivationPrerequisites()
                    )
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.ExpectedSourceMismatch,
                DocumentCacheTargetDiagnosticCategory.ExpectedSourceMismatch
            );
        }

        [TestCase(DocumentCacheDownstreamPublicationStatus.Active)]
        [TestCase(DocumentCacheDownstreamPublicationStatus.Historical)]
        [TestCase(DocumentCacheDownstreamPublicationStatus.Possible)]
        [TestCase(DocumentCacheDownstreamPublicationStatus.Unknown)]
        public void It_should_reject_downstream_history_that_is_not_internal_only(
            DocumentCacheDownstreamPublicationStatus status
        )
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineReadAccelerationActivation(
                    OfflineActivationRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    OfflineActivationFacts(DownstreamObservation(status))
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.DownstreamHistoryPresentOrUnknown,
                DocumentCacheTargetDiagnosticCategory.DownstreamPublicationHistoryPresentOrUnknown
            );
            result.DownstreamPublicationStatus.Should().Be(status);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Offline_Deactivation : DocumentCachePreflightClassifierTests
    {
        [TestCase(DocumentCacheLifecycleState.Tracking)]
        [TestCase(DocumentCacheLifecycleState.Rebuilding)]
        public void It_should_classify_tracking_or_rebuilding_internal_only_targets_as_eligible(
            DocumentCacheLifecycleState lifecycleState
        )
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineDeactivation(
                    OfflineDeactivationRequest(),
                    EligibleObservation(lifecycleState),
                    OfflineDeactivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                    )
                );

            result.Classification.Should().Be(DocumentCacheAdministrativePreflightClassification.Eligible);
            result.Command.Should().Be(DocumentCacheAdministrativeCommand.OfflineDeactivation);
            result.ObservedLifecycle.Should().Be(lifecycleState);
            result
                .DownstreamPublicationStatus.Should()
                .Be(DocumentCacheDownstreamPublicationStatus.InternalOnly);
        }

        [Test]
        public void It_should_reject_disabled_lifecycle_for_deactivation()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineDeactivation(
                    OfflineDeactivationRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    OfflineDeactivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                    )
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.LifecycleMismatch,
                DocumentCacheTargetDiagnosticCategory.LifecycleMismatch
            );
        }

        [Test]
        public void It_should_reject_resetting_lifecycle_for_deactivation()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineDeactivation(
                    OfflineDeactivationRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Resetting),
                    OfflineDeactivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                    )
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.ResettingRequiresExplicitOperatorRecovery,
                DocumentCacheTargetDiagnosticCategory.ResettingRequiresExplicitOperatorRecovery,
                DocumentCacheLifecycleState.Resetting
            );
        }

        [Test]
        public void It_should_reject_active_downstream_history()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineDeactivation(
                    OfflineDeactivationRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Tracking),
                    OfflineDeactivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.Active)
                    )
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.DownstreamHistoryPresentOrUnknown,
                DocumentCacheTargetDiagnosticCategory.DownstreamPublicationHistoryPresentOrUnknown,
                DocumentCacheLifecycleState.Tracking
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Common_Target_Observation_Failures : DocumentCachePreflightClassifierTests
    {
        [Test]
        public void It_should_reject_target_not_configured_after_rollout_removal()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineDeactivation(
                    OfflineDeactivationRequest(),
                    targetObservation: null,
                    OfflineDeactivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                    )
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.TargetNotConfigured,
                DocumentCacheTargetDiagnosticCategory.TargetNotConfigured,
                expectedObservedLifecycle: null
            );
        }

        [Test]
        public void It_should_reject_unresolved_targets()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    UnresolvedObservation(),
                    GuardedFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.TargetUnresolved,
                DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
                expectedObservedLifecycle: null
            );
        }

        [Test]
        public void It_should_reject_target_replacement_before_execution()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    GuardedFacts(expectedTargetContextGeneration: new DocumentCacheTargetContextGeneration(2))
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.TargetReplacedBeforeExecution,
                DocumentCacheTargetDiagnosticCategory.TargetReplaced
            );
        }

        [Test]
        public void It_should_reject_replaced_generation_observations()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    ReplacedObservation(),
                    GuardedFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.TargetReplacedBeforeExecution,
                DocumentCacheTargetDiagnosticCategory.TargetReplaced
            );
        }

        [Test]
        public void It_should_reject_missing_or_invalid_inventory()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    IneligibleInventoryObservation(),
                    GuardedFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.MissingOrInvalidInventory,
                DocumentCacheTargetDiagnosticCategory.InventoryFailure
            );
        }

        [Test]
        public void It_should_reject_unsupported_prerequisite_incidents()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    IneligibleUnsupportedPrerequisiteObservation(),
                    GuardedFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.UnsupportedPrerequisiteIncident,
                DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident,
                expectedObservedLifecycle: DocumentCacheLifecycleState.Tracking
            );
        }

        [Test]
        public void It_should_reject_unexpected_provider_failures_without_leaking_physical_details()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    GuardedFacts(unexpectedProviderFailureMessage: "provider failed\r\nServer=hidden")
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativePreflightClassification.UnexpectedProviderFailure,
                DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure
            );
            result.Diagnostics.Single().Message.Should().NotContain("\r").And.NotContain("\n");
        }
    }

    protected static void AssertRejected(
        DocumentCacheAdministrativeCommandResult result,
        DocumentCacheAdministrativePreflightClassification classification,
        DocumentCacheTargetDiagnosticCategory category,
        DocumentCacheLifecycleState? expectedObservedLifecycle = DocumentCacheLifecycleState.Disabled
    )
    {
        result.Classification.Should().Be(classification);
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Category == category);
        result.NoMutationGuarantee.Should().NotBeNull();
        result.NoMutationGuarantee!.Guaranteed.Should().BeTrue();
        result
            .NoMutationGuarantee.Scope.Should()
            .Be(DocumentCacheAdministrativeNoMutationScope.LifecycleCacheWorkLatchAndProviderSettings);
        result.ObservedLifecycle.Should().Be(expectedObservedLifecycle);

        if (expectedObservedLifecycle is not null)
        {
            result.PhysicalSourceFingerprint.Should().Be(_fingerprint);
            result.TargetContextGeneration.Should().Be(_generation.Value);
        }
    }

    protected static DocumentCacheGuardedNewEmptyActivationRequest GuardedRequest(
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    ) => new(_administrativeTargetKey, expectedPhysicalSourceFingerprint ?? _fingerprint);

    protected static DocumentCacheOfflineReadAccelerationActivationRequest OfflineActivationRequest(
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    ) => new(_administrativeTargetKey, expectedPhysicalSourceFingerprint ?? _fingerprint);

    protected static DocumentCacheOfflineDeactivationRequest OfflineDeactivationRequest(
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    ) => new(_administrativeTargetKey, expectedPhysicalSourceFingerprint ?? _fingerprint);

    protected static DocumentCacheGuardedNewEmptyActivationPreflightFacts GuardedFacts(
        DocumentCacheTargetContextGeneration? expectedTargetContextGeneration = null,
        DocumentCacheProviderPrerequisiteValidationResult? activationProviderPrerequisites = null,
        DocumentCacheGuardedNewEmptyActivationState? guardedNewEmptyState = null,
        string? unexpectedProviderFailureMessage = null
    ) =>
        new(
            expectedTargetContextGeneration ?? _generation,
            activationProviderPrerequisites ?? SatisfiedActivationPrerequisites(),
            guardedNewEmptyState ?? EmptyGuardedNewEmptyState(),
            unexpectedProviderFailureMessage
        );

    protected static DocumentCacheOfflineReadAccelerationActivationPreflightFacts OfflineActivationFacts(
        DocumentCacheDownstreamPublicationHistoryObservation downstreamPublicationHistory,
        DocumentCacheTargetContextGeneration? expectedTargetContextGeneration = null,
        DocumentCacheProviderPrerequisiteValidationResult? activationProviderPrerequisites = null,
        string? unexpectedProviderFailureMessage = null
    ) =>
        new(
            expectedTargetContextGeneration ?? _generation,
            activationProviderPrerequisites ?? SatisfiedActivationPrerequisites(),
            downstreamPublicationHistory,
            unexpectedProviderFailureMessage
        );

    protected static DocumentCacheOfflineDeactivationPreflightFacts OfflineDeactivationFacts(
        DocumentCacheDownstreamPublicationHistoryObservation downstreamPublicationHistory,
        DocumentCacheTargetContextGeneration? expectedTargetContextGeneration = null,
        string? unexpectedProviderFailureMessage = null
    ) =>
        new(
            expectedTargetContextGeneration ?? _generation,
            downstreamPublicationHistory,
            unexpectedProviderFailureMessage
        );

    protected static DocumentCacheGuardedNewEmptyActivationState EmptyGuardedNewEmptyState() =>
        new(canonicalDocumentsEmpty: true, documentCacheEmpty: true, documentProjectionWorkEmpty: true);

    protected static DocumentCacheTargetObservation EligibleObservation(
        DocumentCacheLifecycleState lifecycleState,
        bool cacheAheadRecoveryRequired = false,
        DocumentCacheSqlServerPrerequisiteDetails? sqlServerPrerequisites = null
    ) =>
        DocumentCacheTargetObservation.ResolvedEligible(
            _targetKey,
            _settings,
            _generation,
            RelationalProviderToken.Postgresql,
            _fingerprint,
            new DocumentCacheLifecycleObservation(lifecycleState, cacheAheadRecoveryRequired),
            _satisfiedInventory,
            _satisfiedEnqueueTrigger,
            sqlServerPrerequisites ?? DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );

    protected static DocumentCacheTargetObservation UnresolvedObservation()
    {
        DocumentCacheResolutionRetryState retryState = new(
            attemptCount: 1,
            _observedAt,
            _observedAt + TimeSpan.FromSeconds(30),
            DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
            "Configured target is unresolved."
        );

        return DocumentCacheTargetObservation.Unresolved(
            _targetKey,
            _settings,
            retryState,
            [
                new DocumentCacheTargetDiagnostic(
                    _targetKey,
                    DocumentCacheTargetResolutionState.Unresolved,
                    providerToken: null,
                    generation: null,
                    physicalSourceFingerprint: null,
                    lifecycle: null,
                    inventory: null,
                    enqueueTrigger: null,
                    sqlServerPrerequisites: null,
                    retryState,
                    DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
                    "Configured target is unresolved."
                ),
            ]
        );
    }

    protected static DocumentCacheTargetObservation ReplacedObservation() =>
        DocumentCacheTargetObservation.ReplacedGeneration(
            _targetKey,
            _settings,
            _generation,
            replacedByGeneration: new DocumentCacheTargetContextGeneration(4),
            RelationalProviderToken.Postgresql,
            _fingerprint,
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false),
            _satisfiedInventory,
            _satisfiedEnqueueTrigger,
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable(),
            [
                new DocumentCacheTargetDiagnostic(
                    _targetKey,
                    DocumentCacheTargetResolutionState.ReplacedGeneration,
                    RelationalProviderToken.Postgresql,
                    _generation,
                    _fingerprint,
                    new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false),
                    _satisfiedInventory,
                    _satisfiedEnqueueTrigger,
                    DocumentCacheSqlServerPrerequisiteDetails.NotApplicable(),
                    retryState: null,
                    DocumentCacheTargetDiagnosticCategory.TargetReplaced,
                    "Target generation was replaced."
                ),
            ]
        );

    protected static DocumentCacheTargetObservation IneligibleInventoryObservation()
    {
        DocumentCacheInventoryValidationResult invalidInventory = new(
            DocumentCacheInventoryStatus.Invalid,
            "Inventory invalid."
        );

        return DocumentCacheTargetObservation.ResolvedIneligible(
            _targetKey,
            _settings,
            _generation,
            RelationalProviderToken.Postgresql,
            _fingerprint,
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false),
            invalidInventory,
            _satisfiedEnqueueTrigger,
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable(),
            retryState: null,
            [
                new DocumentCacheTargetDiagnostic(
                    _targetKey,
                    DocumentCacheTargetResolutionState.Resolved,
                    RelationalProviderToken.Postgresql,
                    _generation,
                    _fingerprint,
                    new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false),
                    invalidInventory,
                    _satisfiedEnqueueTrigger,
                    DocumentCacheSqlServerPrerequisiteDetails.NotApplicable(),
                    retryState: null,
                    DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                    "Inventory invalid."
                ),
            ]
        );
    }

    protected static DocumentCacheTargetObservation IneligibleUnsupportedPrerequisiteObservation()
    {
        DocumentCacheLifecycleObservation lifecycle = new(
            DocumentCacheLifecycleState.Tracking,
            CacheAheadRecoveryRequired: false
        );
        DocumentCacheProviderPrerequisiteValidationResult prerequisiteFailure =
            DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                FailedSqlServerPrerequisites(),
                lifecycle
            );

        return DocumentCacheTargetObservation.ResolvedIneligible(
            _targetKey,
            _settings,
            _generation,
            RelationalProviderToken.SqlServer,
            _fingerprint,
            lifecycle,
            _satisfiedInventory,
            _satisfiedEnqueueTrigger,
            prerequisiteFailure.SqlServerPrerequisites,
            retryState: null,
            [
                new DocumentCacheTargetDiagnostic(
                    _targetKey,
                    DocumentCacheTargetResolutionState.Resolved,
                    RelationalProviderToken.SqlServer,
                    _generation,
                    _fingerprint,
                    lifecycle,
                    _satisfiedInventory,
                    _satisfiedEnqueueTrigger,
                    prerequisiteFailure.SqlServerPrerequisites,
                    retryState: null,
                    DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident,
                    prerequisiteFailure.Message
                ),
            ]
        );
    }

    protected static DocumentCacheProviderPrerequisiteValidationResult SatisfiedActivationPrerequisites() =>
        DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );

    protected static DocumentCacheProviderPrerequisiteValidationResult FailedActivationPrerequisites() =>
        DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(FailedSqlServerPrerequisites());

    protected static DocumentCacheSqlServerPrerequisiteDetails FailedSqlServerPrerequisites() =>
        new(
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                DocumentCacheProviderPrerequisiteStatus.Disabled,
                "RCSI disabled."
            ),
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "Nested triggers satisfied."
            )
        );

    protected static DocumentCacheDownstreamPublicationHistoryObservation DownstreamObservation(
        DocumentCacheDownstreamPublicationStatus status,
        DocumentCacheTargetKey? targetKey = null,
        DocumentCachePhysicalSourceFingerprint? fingerprint = null
    ) =>
        new(
            targetKey ?? _targetKey,
            fingerprint ?? _fingerprint,
            status,
            evidenceSource: "fake-binding-store",
            evidenceGenerationIdentifier: "binding-generation-1",
            _observedAt,
            "Downstream history observed."
        );
}
