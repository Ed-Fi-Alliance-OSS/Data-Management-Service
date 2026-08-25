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
    private const string ClassifierNoMutationMessage =
        "Classifier performed no lifecycle cache work latch or provider-setting mutation.";

    private static readonly DateTimeOffset _observedAt = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly DocumentCacheTargetKey _targetKey = DocumentCacheTargetKey.Create("TenantA", 7);

    private static readonly DocumentCacheAdministrativeTargetKey _administrativeTargetKey =
        DocumentCacheAdministrativeTargetKey.FromTargetKey(_targetKey);

    private static readonly DocumentCacheTargetContextGeneration _generation = new(3);

    private static readonly DocumentCachePhysicalSourceFingerprint _fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private static readonly DocumentCachePhysicalSourceFingerprint _otherFingerprint = new(
        "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
    );

    private static readonly DocumentCacheOfflineWriterAdmission _offlineActivationAdmission = new(
        confirmed: true,
        DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
    );

    private static readonly DocumentCacheOfflineWriterAdmission _offlineDeactivationAdmission = new(
        confirmed: true,
        DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
    );

    private static readonly DocumentCacheOfflineWriterAdmission _cacheAheadRecoveryAdmission = new(
        confirmed: true,
        DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained
    );

    private static readonly DocumentCacheTargetEffectiveSettings _settings = new(
        readAccelerationEnabled: true,
        directFillTimeout: TimeSpan.FromMilliseconds(250),
        projectorPollInterval: TimeSpan.FromSeconds(5),
        projectorPageSize: 100,
        projectorMaxConcurrentTargets: 2,
        projectorFailureBackoff: TimeSpan.FromSeconds(30),
        projectorBaselineHighWaterMark: 1000,
        administrationWorkflowTimeout: TimeSpan.FromHours(24)
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

            result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
            result.Command.Should().Be(DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation);
            result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Disabled);
            result.CacheAheadRecoveryRequired.Should().BeFalse();
            result.PhysicalSourceFingerprint.Should().Be(_fingerprint);
            result.TargetGeneration.Should().Be(_generation.Value);
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
                DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
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
                DocumentCacheAdministrativeCommandClassification.ResettingRequiresExplicitOperatorRecovery,
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
                DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
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
                DocumentCacheAdministrativeCommandClassification.NonemptyGuardedActivationState,
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
                DocumentCacheAdministrativeCommandClassification.ProviderPrerequisiteFailed,
                DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed
            );
        }

        [TestCase(
            DocumentCacheLifecycleState.Tracking,
            DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
            DocumentCacheTargetDiagnosticCategory.LifecycleMismatch
        )]
        [TestCase(
            DocumentCacheLifecycleState.Resetting,
            DocumentCacheAdministrativeCommandClassification.ResettingRequiresExplicitOperatorRecovery,
            DocumentCacheTargetDiagnosticCategory.ResettingRequiresExplicitOperatorRecovery
        )]
        public void It_should_reject_lifecycle_state_before_failed_command_time_provider_prerequisites(
            DocumentCacheLifecycleState lifecycleState,
            DocumentCacheAdministrativeCommandClassification expectedClassification,
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
                DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
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
                DocumentCacheAdministrativeCommandClassification.ExpectedSourceMismatch,
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
                DocumentCacheAdministrativeCommandClassification.ExpectedSourceMismatch,
                DocumentCacheTargetDiagnosticCategory.ExpectedSourceMismatch
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Offline_Activation : DocumentCachePreflightClassifierTests
    {
        [TestCaseSource(nameof(InvalidOfflineActivationAdmissions))]
        public void It_should_reject_invalid_offline_writer_admission_before_lifecycle(
            DocumentCacheOfflineWriterAdmission? offlineWriterAdmission,
            DocumentCacheAdministrativeCommandClassification expectedClassification,
            DocumentCacheAdministrativeDiagnosticCategory expectedDiagnosticCategory
        )
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineActivation(
                    new DocumentCacheOfflineActivationRequest(
                        _administrativeTargetKey,
                        offlineWriterAdmission,
                        _fingerprint
                    ),
                    EligibleObservation(DocumentCacheLifecycleState.Tracking),
                    OfflineActivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                    )
                );

            AssertOfflineWriterAdmissionRejected(
                result,
                expectedClassification,
                expectedDiagnosticCategory,
                DocumentCacheLifecycleState.Tracking
            );
        }

        [Test]
        [TestCase(DocumentCacheLifecycleState.Disabled)]
        [TestCase(DocumentCacheLifecycleState.Rebuilding)]
        public void It_should_classify_disabled_or_rebuilding_internal_only_targets_as_eligible(
            DocumentCacheLifecycleState lifecycleState
        )
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineActivation(
                    OfflineActivationRequest(),
                    EligibleObservation(lifecycleState),
                    OfflineActivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                    )
                );

            result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
            result.Command.Should().Be(DocumentCacheAdministrativeCommand.OfflineActivation);
            result
                .DownstreamPublicationStatus.Should()
                .Be(DocumentCacheDownstreamPublicationStatus.InternalOnly);
            result.Lifecycle.Should().Be(lifecycleState);
        }

        [Test]
        public void It_should_reject_cache_ahead_latch_before_offline_activation()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineActivation(
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
                DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
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
                DocumentCachePreflightClassifier.ClassifyOfflineActivation(
                    OfflineActivationRequest(),
                    startupEligibleObservation,
                    OfflineActivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly),
                        activationProviderPrerequisites: FailedActivationPrerequisites()
                    )
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.ProviderPrerequisiteFailed,
                DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed
            );
        }

        [TestCase(
            DocumentCacheLifecycleState.Tracking,
            DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
            DocumentCacheTargetDiagnosticCategory.LifecycleMismatch
        )]
        [TestCase(
            DocumentCacheLifecycleState.Resetting,
            DocumentCacheAdministrativeCommandClassification.ResettingRequiresExplicitOperatorRecovery,
            DocumentCacheTargetDiagnosticCategory.ResettingRequiresExplicitOperatorRecovery
        )]
        public void It_should_reject_lifecycle_state_before_failed_command_time_provider_prerequisites(
            DocumentCacheLifecycleState lifecycleState,
            DocumentCacheAdministrativeCommandClassification expectedClassification,
            DocumentCacheTargetDiagnosticCategory expectedDiagnosticCategory
        )
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineActivation(
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
                DocumentCachePreflightClassifier.ClassifyOfflineActivation(
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
                DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
                DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet
            );
        }

        [Test]
        public void It_should_reject_expected_source_mismatch_before_failed_command_time_provider_prerequisites()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineActivation(
                    OfflineActivationRequest(expectedPhysicalSourceFingerprint: _otherFingerprint),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    OfflineActivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly),
                        activationProviderPrerequisites: FailedActivationPrerequisites()
                    )
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.ExpectedSourceMismatch,
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
                DocumentCachePreflightClassifier.ClassifyOfflineActivation(
                    OfflineActivationRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    OfflineActivationFacts(DownstreamObservation(status))
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown,
                DocumentCacheTargetDiagnosticCategory.DownstreamPublicationHistoryPresentOrUnknown
            );
            result.DownstreamPublicationStatus.Should().Be(status);
        }

        private static IEnumerable<TestCaseData> InvalidOfflineActivationAdmissions()
        {
            yield return new TestCaseData(
                null,
                DocumentCacheAdministrativeCommandClassification.MissingOfflineWriterAdmission,
                DocumentCacheAdministrativeDiagnosticCategory.MissingOfflineWriterAdmission
            ).SetName("Missing admission");

            yield return new TestCaseData(
                new DocumentCacheOfflineWriterAdmission(
                    confirmed: false,
                    DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
                ),
                DocumentCacheAdministrativeCommandClassification.UnconfirmedOfflineWriterAdmission,
                DocumentCacheAdministrativeDiagnosticCategory.UnconfirmedOfflineWriterAdmission
            ).SetName("Unconfirmed admission");

            yield return new TestCaseData(
                new DocumentCacheOfflineWriterAdmission(
                    confirmed: true,
                    DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
                ),
                DocumentCacheAdministrativeCommandClassification.MismatchedOfflineWriterAdmission,
                DocumentCacheAdministrativeDiagnosticCategory.MismatchedOfflineWriterAdmission
            ).SetName("Mismatched admission");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Offline_Deactivation : DocumentCachePreflightClassifierTests
    {
        [Test]
        public void It_should_reject_unconfirmed_offline_writer_admission_before_lifecycle()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineDeactivation(
                    new DocumentCacheOfflineDeactivationRequest(
                        _administrativeTargetKey,
                        new DocumentCacheOfflineWriterAdmission(
                            confirmed: false,
                            DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
                        ),
                        _fingerprint
                    ),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    OfflineDeactivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                    )
                );

            AssertOfflineWriterAdmissionRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.UnconfirmedOfflineWriterAdmission,
                DocumentCacheAdministrativeDiagnosticCategory.UnconfirmedOfflineWriterAdmission,
                DocumentCacheLifecycleState.Disabled
            );
        }

        [TestCase(DocumentCacheLifecycleState.Tracking)]
        [TestCase(DocumentCacheLifecycleState.Resetting)]
        [TestCase(DocumentCacheLifecycleState.Rebuilding)]
        public void It_should_classify_tracking_resetting_or_rebuilding_clear_latch_internal_only_targets_as_eligible(
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

            result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
            result.Command.Should().Be(DocumentCacheAdministrativeCommand.OfflineDeactivation);
            result.Lifecycle.Should().Be(lifecycleState);
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
                DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
                DocumentCacheTargetDiagnosticCategory.LifecycleMismatch
            );
        }

        [Test]
        [TestCase(DocumentCacheLifecycleState.Tracking)]
        [TestCase(DocumentCacheLifecycleState.Resetting)]
        [TestCase(DocumentCacheLifecycleState.Rebuilding)]
        public void It_should_reject_a_set_cache_ahead_latch_before_deactivation(
            DocumentCacheLifecycleState lifecycleState
        )
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOfflineDeactivation(
                    OfflineDeactivationRequest(),
                    EligibleObservation(lifecycleState, cacheAheadRecoveryRequired: true),
                    OfflineDeactivationFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                    )
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
                DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet,
                lifecycleState
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
                DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown,
                DocumentCacheTargetDiagnosticCategory.DownstreamPublicationHistoryPresentOrUnknown,
                DocumentCacheLifecycleState.Tracking
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Online_Cache_Rebuild : DocumentCachePreflightClassifierTests
    {
        [TestCase(DocumentCacheLifecycleState.Tracking)]
        [TestCase(DocumentCacheLifecycleState.Resetting)]
        [TestCase(DocumentCacheLifecycleState.Rebuilding)]
        public void It_should_classify_tracking_resetting_or_rebuilding_clear_latch_targets_as_eligible(
            DocumentCacheLifecycleState lifecycleState
        )
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOnlineCacheRebuild(
                    OnlineCacheRebuildRequest(),
                    EligibleObservation(lifecycleState),
                    OnlineCacheRebuildFacts()
                );

            result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
            result.Command.Should().Be(DocumentCacheAdministrativeCommand.OnlineCacheRebuild);
            result.Lifecycle.Should().Be(lifecycleState);
            result.CacheAheadRecoveryRequired.Should().BeFalse();
            result.NoMutationGuarantee.Should().BeNull();
        }

        [Test]
        public void It_should_reject_disabled_lifecycle()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOnlineCacheRebuild(
                    OnlineCacheRebuildRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    OnlineCacheRebuildFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
                DocumentCacheTargetDiagnosticCategory.LifecycleMismatch
            );
        }

        [TestCase(DocumentCacheLifecycleState.Tracking)]
        [TestCase(DocumentCacheLifecycleState.Resetting)]
        [TestCase(DocumentCacheLifecycleState.Rebuilding)]
        public void It_should_reject_a_set_cache_ahead_latch_before_lifecycle_resume(
            DocumentCacheLifecycleState lifecycleState
        )
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOnlineCacheRebuild(
                    OnlineCacheRebuildRequest(),
                    EligibleObservation(lifecycleState, cacheAheadRecoveryRequired: true),
                    OnlineCacheRebuildFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
                DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet,
                lifecycleState
            );
        }

        [Test]
        public void It_should_reject_expected_source_mismatch()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyOnlineCacheRebuild(
                    OnlineCacheRebuildRequest(expectedPhysicalSourceFingerprint: _otherFingerprint),
                    EligibleObservation(DocumentCacheLifecycleState.Tracking),
                    OnlineCacheRebuildFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.ExpectedSourceMismatch,
                DocumentCacheTargetDiagnosticCategory.ExpectedSourceMismatch,
                DocumentCacheLifecycleState.Tracking
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Explicit_Integrity_Scrub : DocumentCachePreflightClassifierTests
    {
        [Test]
        public void It_should_classify_tracking_clear_latch_targets_as_eligible()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyExplicitIntegrityScrub(
                    ExplicitIntegrityScrubRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Tracking),
                    ExplicitIntegrityScrubFacts()
                );

            result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
            result.Command.Should().Be(DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub);
            result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
            result.CacheAheadRecoveryRequired.Should().BeFalse();
            result.NoMutationGuarantee.Should().BeNull();
        }

        [TestCase(DocumentCacheLifecycleState.Disabled)]
        [TestCase(DocumentCacheLifecycleState.Rebuilding)]
        public void It_should_reject_lifecycle_mismatches(DocumentCacheLifecycleState lifecycleState)
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyExplicitIntegrityScrub(
                    ExplicitIntegrityScrubRequest(),
                    EligibleObservation(lifecycleState),
                    ExplicitIntegrityScrubFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
                DocumentCacheTargetDiagnosticCategory.LifecycleMismatch,
                lifecycleState
            );
        }

        [Test]
        public void It_should_reject_resetting_as_explicit_operator_recovery()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyExplicitIntegrityScrub(
                    ExplicitIntegrityScrubRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Resetting),
                    ExplicitIntegrityScrubFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.ResettingRequiresExplicitOperatorRecovery,
                DocumentCacheTargetDiagnosticCategory.ResettingRequiresExplicitOperatorRecovery,
                DocumentCacheLifecycleState.Resetting
            );
        }

        [Test]
        public void It_should_reject_a_set_cache_ahead_latch()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyExplicitIntegrityScrub(
                    ExplicitIntegrityScrubRequest(),
                    EligibleObservation(
                        DocumentCacheLifecycleState.Tracking,
                        cacheAheadRecoveryRequired: true
                    ),
                    ExplicitIntegrityScrubFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
                DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet,
                DocumentCacheLifecycleState.Tracking
            );
        }

        [Test]
        public void It_should_reject_expected_source_mismatch()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyExplicitIntegrityScrub(
                    ExplicitIntegrityScrubRequest(expectedPhysicalSourceFingerprint: _otherFingerprint),
                    EligibleObservation(DocumentCacheLifecycleState.Tracking),
                    ExplicitIntegrityScrubFacts()
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.ExpectedSourceMismatch,
                DocumentCacheTargetDiagnosticCategory.ExpectedSourceMismatch,
                DocumentCacheLifecycleState.Tracking
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Internal_Only_Cache_Ahead_Recovery : DocumentCachePreflightClassifierTests
    {
        [Test]
        public void It_should_reject_mismatched_offline_writer_admission_before_lifecycle()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyInternalOnlyCacheAheadRecovery(
                    new DocumentCacheInternalOnlyCacheAheadRecoveryRequest(
                        _administrativeTargetKey,
                        _offlineActivationAdmission,
                        _fingerprint
                    ),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    InternalOnlyCacheAheadRecoveryFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                    )
                );

            AssertOfflineWriterAdmissionRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.MismatchedOfflineWriterAdmission,
                DocumentCacheAdministrativeDiagnosticCategory.MismatchedOfflineWriterAdmission,
                DocumentCacheLifecycleState.Disabled
            );
        }

        [TestCase(DocumentCacheLifecycleState.Tracking)]
        [TestCase(DocumentCacheLifecycleState.Resetting)]
        [TestCase(DocumentCacheLifecycleState.Rebuilding)]
        public void It_should_classify_set_latch_recovery_states_as_eligible(
            DocumentCacheLifecycleState lifecycleState
        )
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyInternalOnlyCacheAheadRecovery(
                    InternalOnlyCacheAheadRecoveryRequest(),
                    EligibleObservation(lifecycleState, cacheAheadRecoveryRequired: true),
                    InternalOnlyCacheAheadRecoveryFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                    )
                );

            result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
            result.Command.Should().Be(DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery);
            result.Lifecycle.Should().Be(lifecycleState);
            result.CacheAheadRecoveryRequired.Should().BeTrue();
            result
                .DownstreamPublicationStatus.Should()
                .Be(DocumentCacheDownstreamPublicationStatus.InternalOnly);
            result.NoMutationGuarantee.Should().BeNull();
        }

        [Test]
        public void It_should_classify_rebuilding_clear_latch_as_the_supported_resume_state()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyInternalOnlyCacheAheadRecovery(
                    InternalOnlyCacheAheadRecoveryRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Rebuilding),
                    InternalOnlyCacheAheadRecoveryFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                    )
                );

            result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
            result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Rebuilding);
            result.CacheAheadRecoveryRequired.Should().BeFalse();
        }

        [TestCase(DocumentCacheLifecycleState.Disabled, false)]
        [TestCase(DocumentCacheLifecycleState.Tracking, false)]
        [TestCase(DocumentCacheLifecycleState.Resetting, false)]
        [TestCase(DocumentCacheLifecycleState.Disabled, true)]
        public void It_should_reject_unsupported_lifecycle_or_latch_combinations(
            DocumentCacheLifecycleState lifecycleState,
            bool cacheAheadRecoveryRequired
        )
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyInternalOnlyCacheAheadRecovery(
                    InternalOnlyCacheAheadRecoveryRequest(),
                    EligibleObservation(lifecycleState, cacheAheadRecoveryRequired),
                    InternalOnlyCacheAheadRecoveryFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                    )
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
                DocumentCacheTargetDiagnosticCategory.LifecycleMismatch,
                lifecycleState
            );
            result.CacheAheadRecoveryRequired.Should().Be(cacheAheadRecoveryRequired);
        }

        [Test]
        public void It_should_reject_non_internal_downstream_history()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyInternalOnlyCacheAheadRecovery(
                    InternalOnlyCacheAheadRecoveryRequest(),
                    EligibleObservation(
                        DocumentCacheLifecycleState.Tracking,
                        cacheAheadRecoveryRequired: true
                    ),
                    InternalOnlyCacheAheadRecoveryFacts(
                        DownstreamObservation(DocumentCacheDownstreamPublicationStatus.Possible)
                    )
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown,
                DocumentCacheTargetDiagnosticCategory.DownstreamPublicationHistoryPresentOrUnknown,
                DocumentCacheLifecycleState.Tracking
            );
            result.CacheAheadRecoveryRequired.Should().BeTrue();
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
                DocumentCacheAdministrativeCommandClassification.TargetNotConfigured,
                DocumentCacheTargetDiagnosticCategory.TargetNotConfigured,
                expectedLifecycle: null
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
                DocumentCacheAdministrativeCommandClassification.TargetUnresolved,
                DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
                expectedLifecycle: null
            );
        }

        [Test]
        public void It_should_reject_current_observations_when_expected_generation_is_stale()
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    EligibleObservation(DocumentCacheLifecycleState.Disabled),
                    GuardedFacts(expectedTargetContextGeneration: new DocumentCacheTargetContextGeneration(2))
                );

            AssertRejected(
                result,
                DocumentCacheAdministrativeCommandClassification.TargetReplacedBeforeExecution,
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
                DocumentCacheAdministrativeCommandClassification.MissingOrInvalidInventory,
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
                DocumentCacheAdministrativeCommandClassification.UnsupportedPrerequisiteIncident,
                DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident,
                expectedLifecycle: DocumentCacheLifecycleState.Tracking
            );
        }

        [TestCaseSource(nameof(TargetContextFailureClassifications))]
        public void It_should_preserve_specific_target_context_failure_classifications(
            DocumentCacheTargetDiagnosticCategory diagnosticCategory,
            DocumentCacheAdministrativeCommandClassification expectedClassification
        )
        {
            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    GuardedRequest(),
                    IneligibleTargetContextObservation(diagnosticCategory),
                    GuardedFacts()
                );

            AssertRejected(result, expectedClassification, diagnosticCategory, expectedLifecycle: null);
            result
                .Diagnostics.Should()
                .ContainSingle()
                .Which.Category.Should()
                .Be(Enum.Parse<DocumentCacheAdministrativeDiagnosticCategory>(diagnosticCategory.ToString()));
            result.TargetGeneration.Should().Be(_generation.Value);
            result.PhysicalSourceFingerprint.Should().BeNull();
        }

        [Test]
        public void It_should_classify_target_observation_failures_without_command_specific_facts()
        {
            DocumentCacheAdministrativeCommandResult? result =
                DocumentCachePreflightClassifier.ClassifyTargetObservationFailure(
                    DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                    _administrativeTargetKey,
                    IneligibleTargetContextObservation(
                        DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing
                    )
                );

            result.Should().NotBeNull();
            AssertRejected(
                result!,
                DocumentCacheAdministrativeCommandClassification.ProviderMetadataMissing,
                DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing,
                expectedLifecycle: null
            );
        }

        [Test]
        public void It_should_not_reject_eligible_target_observations_without_command_specific_facts()
        {
            DocumentCacheAdministrativeCommandResult? result =
                DocumentCachePreflightClassifier.ClassifyTargetObservationFailure(
                    DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                    _administrativeTargetKey,
                    EligibleObservation(DocumentCacheLifecycleState.Tracking)
                );

            result.Should().BeNull();
        }

        [Test]
        public void It_should_classify_unmapped_ineligible_target_context_as_unexpected_provider_failure()
        {
            DocumentCacheAdministrativeCommandResult? result =
                DocumentCachePreflightClassifier.ClassifyTargetObservationFailure(
                    DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                    _administrativeTargetKey,
                    IneligibleUnexpectedProviderFailureObservation()
                );

            result.Should().NotBeNull();
            AssertRejected(
                result!,
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure,
                expectedLifecycle: null
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
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure
            );
            result.Diagnostics.Single().Message.Should().NotContain("\r").And.NotContain("\n");
        }

        private static IEnumerable<TestCaseData> TargetContextFailureClassifications()
        {
            yield return new TestCaseData(
                DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing,
                DocumentCacheAdministrativeCommandClassification.ProviderMetadataMissing
            ).SetName("Provider metadata missing");
            yield return new TestCaseData(
                DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown,
                DocumentCacheAdministrativeCommandClassification.ProviderMetadataUnknown
            ).SetName("Provider metadata unknown");
            yield return new TestCaseData(
                DocumentCacheTargetDiagnosticCategory.ProviderMismatch,
                DocumentCacheAdministrativeCommandClassification.ProviderMismatch
            ).SetName("Provider mismatch");
            yield return new TestCaseData(
                DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing,
                DocumentCacheAdministrativeCommandClassification.ConnectionInputMissing
            ).SetName("Connection input missing");
        }
    }

    protected static void AssertRejected(
        DocumentCacheAdministrativeCommandResult result,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheTargetDiagnosticCategory category,
        DocumentCacheLifecycleState? expectedLifecycle = DocumentCacheLifecycleState.Disabled
    )
    {
        result.Classification.Should().Be(classification);
        DocumentCacheAdministrativeDiagnosticCategory expectedCategory =
            Enum.Parse<DocumentCacheAdministrativeDiagnosticCategory>(category.ToString());
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Category == expectedCategory);
        result.NoMutationGuarantee.Should().NotBeNull();
        result.NoMutationGuarantee!.Guaranteed.Should().BeTrue();
        result.NoMutationGuarantee.Message.Should().Be(ClassifierNoMutationMessage);
        result
            .NoMutationGuarantee.Scope.Should()
            .Be(DocumentCacheAdministrativeNoMutationScope.LifecycleCacheWorkLatchAndProviderSettings);
        result.Lifecycle.Should().Be(expectedLifecycle);

        if (expectedLifecycle is not null)
        {
            result.PhysicalSourceFingerprint.Should().Be(_fingerprint);
            result.TargetGeneration.Should().Be(_generation.Value);
        }
    }

    protected static void AssertOfflineWriterAdmissionRejected(
        DocumentCacheAdministrativeCommandResult result,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        DocumentCacheLifecycleState expectedLifecycle
    )
    {
        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result.Classification.Should().Be(classification);
        result.Mutated.Should().BeFalse();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Category == diagnosticCategory);
        result
            .PhaseDiagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.Preflight
                && diagnostic.DiagnosticCategory == diagnosticCategory
            );
        result.NoMutationGuarantee.Should().NotBeNull();
        result.NoMutationGuarantee!.Guaranteed.Should().BeTrue();
        result.NoMutationGuarantee.Message.Should().Be(ClassifierNoMutationMessage);
        result.Lifecycle.Should().Be(expectedLifecycle);
        result.PhysicalSourceFingerprint.Should().Be(_fingerprint);
        result.TargetGeneration.Should().Be(_generation.Value);
    }

    protected static DocumentCacheGuardedNewEmptyActivationRequest GuardedRequest(
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    ) => new(_administrativeTargetKey, expectedPhysicalSourceFingerprint ?? _fingerprint);

    protected static DocumentCacheOfflineActivationRequest OfflineActivationRequest(
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    ) =>
        new(
            _administrativeTargetKey,
            _offlineActivationAdmission,
            expectedPhysicalSourceFingerprint ?? _fingerprint
        );

    protected static DocumentCacheOfflineDeactivationRequest OfflineDeactivationRequest(
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    ) =>
        new(
            _administrativeTargetKey,
            _offlineDeactivationAdmission,
            expectedPhysicalSourceFingerprint ?? _fingerprint
        );

    protected static DocumentCacheOnlineCacheRebuildRequest OnlineCacheRebuildRequest(
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    ) => new(_administrativeTargetKey, expectedPhysicalSourceFingerprint ?? _fingerprint);

    protected static DocumentCacheExplicitIntegrityScrubRequest ExplicitIntegrityScrubRequest(
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    ) => new(_administrativeTargetKey, expectedPhysicalSourceFingerprint ?? _fingerprint);

    protected static DocumentCacheInternalOnlyCacheAheadRecoveryRequest InternalOnlyCacheAheadRecoveryRequest(
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    ) =>
        new(
            _administrativeTargetKey,
            _cacheAheadRecoveryAdmission,
            expectedPhysicalSourceFingerprint ?? _fingerprint
        );

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

    protected static DocumentCacheOfflineActivationPreflightFacts OfflineActivationFacts(
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

    protected static DocumentCacheOnlineCacheRebuildPreflightFacts OnlineCacheRebuildFacts(
        DocumentCacheTargetContextGeneration? expectedTargetContextGeneration = null,
        string? unexpectedProviderFailureMessage = null
    ) => new(expectedTargetContextGeneration ?? _generation, unexpectedProviderFailureMessage);

    protected static DocumentCacheExplicitIntegrityScrubPreflightFacts ExplicitIntegrityScrubFacts(
        DocumentCacheTargetContextGeneration? expectedTargetContextGeneration = null,
        string? unexpectedProviderFailureMessage = null
    ) => new(expectedTargetContextGeneration ?? _generation, unexpectedProviderFailureMessage);

    protected static DocumentCacheInternalOnlyCacheAheadRecoveryPreflightFacts InternalOnlyCacheAheadRecoveryFacts(
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

    protected static DocumentCacheTargetObservation IneligibleTargetContextObservation(
        DocumentCacheTargetDiagnosticCategory category
    )
    {
        RelationalProviderToken? providerToken = category switch
        {
            DocumentCacheTargetDiagnosticCategory.ProviderMismatch => RelationalProviderToken.SqlServer,
            DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing =>
                RelationalProviderToken.Postgresql,
            _ => null,
        };

        string message = category switch
        {
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing =>
                "Resolved target is missing relational provider metadata.",
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown =>
                "Resolved target has unknown relational provider metadata.",
            DocumentCacheTargetDiagnosticCategory.ProviderMismatch =>
                "Resolved target provider does not match this DMS process provider.",
            DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing =>
                "Resolved target has no usable connection input.",
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
        };

        return DocumentCacheTargetObservation.ResolvedIneligible(
            _targetKey,
            _settings,
            _generation,
            providerToken,
            physicalSourceFingerprint: null,
            lifecycle: null,
            inventory: null,
            enqueueTrigger: null,
            sqlServerPrerequisites: null,
            retryState: null,
            [
                new DocumentCacheTargetDiagnostic(
                    _targetKey,
                    DocumentCacheTargetResolutionState.Resolved,
                    providerToken,
                    _generation,
                    physicalSourceFingerprint: null,
                    lifecycle: null,
                    inventory: null,
                    enqueueTrigger: null,
                    sqlServerPrerequisites: null,
                    retryState: null,
                    category,
                    message
                ),
            ]
        );
    }

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

    protected static DocumentCacheTargetObservation IneligibleUnexpectedProviderFailureObservation() =>
        DocumentCacheTargetObservation.ResolvedIneligible(
            _targetKey,
            _settings,
            _generation,
            RelationalProviderToken.Postgresql,
            physicalSourceFingerprint: null,
            lifecycle: null,
            inventory: null,
            enqueueTrigger: null,
            sqlServerPrerequisites: null,
            retryState: null,
            [
                new DocumentCacheTargetDiagnostic(
                    _targetKey,
                    DocumentCacheTargetResolutionState.Resolved,
                    RelationalProviderToken.Postgresql,
                    _generation,
                    physicalSourceFingerprint: null,
                    lifecycle: null,
                    inventory: null,
                    enqueueTrigger: null,
                    sqlServerPrerequisites: null,
                    retryState: null,
                    DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure,
                    "Resolved target failed for an unexpected provider reason."
                ),
            ]
        );

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
