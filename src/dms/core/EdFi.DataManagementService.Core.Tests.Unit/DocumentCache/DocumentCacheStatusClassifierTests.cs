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
[Category("DocumentCacheStatus")]
public class DocumentCacheStatusClassifierTests
{
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("TenantA", 7);
    private static readonly DateTimeOffset ProcessObservedAt = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DurableObservedAt = new(2026, 8, 17, 12, 0, 1, TimeSpan.Zero);
    private static readonly DateTimeOffset OldestWorkFirstEnqueuedAt = DurableObservedAt.AddMinutes(-5);

    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private static readonly DocumentCacheTargetEffectiveSettings EffectiveSettings = new(
        readAccelerationEnabled: true,
        directFillTimeout: TimeSpan.FromSeconds(2),
        projectorPollInterval: TimeSpan.FromSeconds(5),
        projectorPageSize: 100,
        projectorMaxConcurrentTargets: 4,
        projectorFailureBackoff: TimeSpan.FromSeconds(30),
        projectorBaselineHighWaterMark: 10000,
        administrationWorkflowTimeout: TimeSpan.FromMinutes(10)
    );

    private static readonly DocumentCacheInventoryValidationResult SatisfiedInventory = new(
        DocumentCacheInventoryStatus.Satisfied,
        "Inventory satisfied."
    );

    private static readonly DocumentCacheEnqueueTriggerValidationResult SatisfiedEnqueueTrigger = new(
        DocumentCacheEnqueueTriggerStatus.Satisfied,
        "Enqueue trigger satisfied."
    );

    private static readonly DocumentCacheLifecycleObservation TrackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );

    [Test]
    public void It_classifies_tracking_clear_empty_queue_as_operational_and_caught_up()
    {
        DocumentCacheStatusClassificationResult result = Classify(
            EligibleTarget(),
            RunningRuntime(),
            DocumentCacheStatusDurableObservation.Success(
                DocumentCacheLifecycleState.Tracking,
                cacheAheadRecoveryRequired: false,
                DocumentCacheStatusQueuePresence.Empty,
                oldestWorkFirstEnqueuedAt: null,
                oldestWorkAgeSeconds: null,
                DurableObservedAt
            )
        );

        result.ProcessEligibility.IsEligible.Should().BeTrue();
        result.DurableObservationRequired.Should().BeFalse();
        result.DurableObservedAt.Should().Be(DurableObservedAt);
        result.Lifecycle.State.Should().Be(DocumentCacheStatusLifecycleState.Tracking);
        result.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Available);
        result.CacheAhead.State.Should().Be(DocumentCacheStatusCacheAheadState.Clear);
        result.CacheAhead.RecoveryRequired.Should().BeFalse();
        result.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Empty);
        result.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Operational);
        result.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.None);
        result.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.CaughtUp);
        result.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.None);
    }

    [Test]
    public void It_keeps_non_empty_queue_presence_out_of_operational_health()
    {
        DocumentCacheStatusClassificationResult result = Classify(
            EligibleTarget(),
            RunningRuntime(),
            DocumentCacheStatusDurableObservation.Success(
                DocumentCacheLifecycleState.Tracking,
                cacheAheadRecoveryRequired: false,
                DocumentCacheStatusQueuePresence.NotEmpty,
                OldestWorkFirstEnqueuedAt,
                oldestWorkAgeSeconds: 300,
                DurableObservedAt
            )
        );

        result.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Operational);
        result.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.None);
        result.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.NotCaughtUp);
        result.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.QueueNotEmpty);
        result.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.NotEmpty);
        result.QueueSummary.OldestWorkFirstEnqueuedAt.Should().Be(OldestWorkFirstEnqueuedAt);
        result.QueueSummary.OldestWorkAgeSeconds.Should().Be(300);
    }

    [TestCase(DocumentCacheLifecycleState.Disabled, DocumentCacheStatusReason.LifecycleDisabled)]
    [TestCase(DocumentCacheLifecycleState.Resetting, DocumentCacheStatusReason.LifecycleResetting)]
    [TestCase(DocumentCacheLifecycleState.Rebuilding, DocumentCacheStatusReason.LifecycleRebuilding)]
    public void It_classifies_known_non_operational_lifecycle_states(
        DocumentCacheLifecycleState lifecycleState,
        DocumentCacheStatusReason expectedReason
    )
    {
        DocumentCacheStatusClassificationResult result = Classify(
            EligibleTarget(),
            RunningRuntime(),
            DocumentCacheStatusDurableObservation.Success(
                lifecycleState,
                cacheAheadRecoveryRequired: true,
                DocumentCacheStatusQueuePresence.NotEmpty,
                OldestWorkFirstEnqueuedAt,
                oldestWorkAgeSeconds: 300,
                DurableObservedAt
            )
        );

        result.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.NonOperational);
        result.OperationalHealth.Reason.Should().Be(expectedReason);
        result.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.NotCaughtUp);
        result.CaughtUp.Reason.Should().Be(expectedReason);
    }

    [Test]
    public void It_classifies_cache_ahead_recovery_latch_after_tracking_lifecycle()
    {
        DocumentCacheStatusClassificationResult result = Classify(
            EligibleTarget(),
            RunningRuntime(),
            DocumentCacheStatusDurableObservation.Success(
                DocumentCacheLifecycleState.Tracking,
                cacheAheadRecoveryRequired: true,
                DocumentCacheStatusQueuePresence.Empty,
                oldestWorkFirstEnqueuedAt: null,
                oldestWorkAgeSeconds: null,
                DurableObservedAt
            )
        );

        result.Lifecycle.State.Should().Be(DocumentCacheStatusLifecycleState.Tracking);
        result.CacheAhead.State.Should().Be(DocumentCacheStatusCacheAheadState.RecoveryRequired);
        result.CacheAhead.RecoveryRequired.Should().BeTrue();
        result.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.NonOperational);
        result.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.CacheAheadRecoveryRequired);
        result.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.NotCaughtUp);
        result.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.CacheAheadRecoveryRequired);
    }

    [Test]
    public void It_classifies_missing_or_invalid_state_as_known_non_operational()
    {
        DocumentCacheStatusClassificationResult result = Classify(
            EligibleTarget(),
            RunningRuntime(),
            DocumentCacheStatusDurableObservation.StateMissingOrInvalid(
                DurableObservedAt,
                "DocumentCacheState row was missing."
            )
        );

        result.DurableObservedAt.Should().Be(DurableObservedAt);
        result.Lifecycle.State.Should().Be(DocumentCacheStatusLifecycleState.Invalid);
        result.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Available);
        result.CacheAhead.State.Should().Be(DocumentCacheStatusCacheAheadState.Unknown);
        result.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unknown);
        result.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.NonOperational);
        result.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.StateMissingOrInvalid);
        result.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.NotCaughtUp);
        result.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.StateMissingOrInvalid);
    }

    [Test]
    public void It_preserves_observed_empty_queue_facts_when_state_is_missing_or_invalid()
    {
        DocumentCacheStatusClassificationResult result = Classify(
            EligibleTarget(),
            RunningRuntime(),
            DocumentCacheStatusDurableObservation.StateMissingOrInvalid(
                DurableObservedAt,
                DocumentCacheStatusQueuePresence.Empty,
                oldestWorkFirstEnqueuedAt: null,
                oldestWorkAgeSeconds: null,
                "DocumentCacheState row was missing."
            )
        );

        result.DurableObservedAt.Should().Be(DurableObservedAt);
        result.Lifecycle.State.Should().Be(DocumentCacheStatusLifecycleState.Invalid);
        result.CacheAhead.State.Should().Be(DocumentCacheStatusCacheAheadState.Unknown);
        result.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Empty);
        result.QueueSummary.OldestWorkFirstEnqueuedAt.Should().BeNull();
        result.QueueSummary.OldestWorkAgeSeconds.Should().BeNull();
        result.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.NonOperational);
        result.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.StateMissingOrInvalid);
        result.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.NotCaughtUp);
        result.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.StateMissingOrInvalid);
    }

    [Test]
    public void It_preserves_observed_nonempty_queue_facts_when_state_is_missing_or_invalid()
    {
        DocumentCacheStatusClassificationResult result = Classify(
            EligibleTarget(),
            RunningRuntime(),
            DocumentCacheStatusDurableObservation.StateMissingOrInvalid(
                DurableObservedAt,
                DocumentCacheStatusQueuePresence.NotEmpty,
                OldestWorkFirstEnqueuedAt,
                oldestWorkAgeSeconds: 300,
                "DocumentCacheState row was missing."
            )
        );

        result.DurableObservedAt.Should().Be(DurableObservedAt);
        result.Lifecycle.State.Should().Be(DocumentCacheStatusLifecycleState.Invalid);
        result.CacheAhead.State.Should().Be(DocumentCacheStatusCacheAheadState.Unknown);
        result.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.NotEmpty);
        result.QueueSummary.OldestWorkFirstEnqueuedAt.Should().Be(OldestWorkFirstEnqueuedAt);
        result.QueueSummary.OldestWorkAgeSeconds.Should().Be(300);
        result.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.NonOperational);
        result.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.StateMissingOrInvalid);
        result.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.NotCaughtUp);
        result.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.StateMissingOrInvalid);
    }

    [Test]
    public void It_uses_unavailable_durable_fields_when_process_eligibility_fails()
    {
        DocumentCacheStatusClassificationResult result = Classify(
            TargetWithFailures([DocumentCacheStatusReason.InventoryInvalid]),
            RunningRuntime(),
            DocumentCacheStatusDurableObservation.Success(
                DocumentCacheLifecycleState.Tracking,
                cacheAheadRecoveryRequired: false,
                DocumentCacheStatusQueuePresence.Empty,
                oldestWorkFirstEnqueuedAt: null,
                oldestWorkAgeSeconds: null,
                DurableObservedAt
            )
        );

        result.ProcessEligibility.Status.Should().Be(DocumentCacheStatusProcessEligibilityStatus.Ineligible);
        result.ProcessEligibility.Reason.Should().Be(DocumentCacheStatusReason.InventoryInvalid);
        result.DurableObservedAt.Should().BeNull();
        result.Lifecycle.State.Should().Be(DocumentCacheStatusLifecycleState.Unknown);
        result.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unavailable);
        result.CacheAhead.RecoveryRequired.Should().BeNull();
        result.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unavailable);
        result.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.NonOperational);
        result.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.InventoryInvalid);
        result.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.NotCaughtUp);
        result.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.InventoryInvalid);
    }

    [TestCase(
        DocumentCacheLifecycleReadStatus.Missing,
        DocumentCacheStatusReason.StateMissingOrInvalid,
        DocumentCacheStatusProcessEligibilityStatus.Ineligible,
        DocumentCacheOperationalHealthStatus.NonOperational,
        DocumentCacheCaughtUpStatus.NotCaughtUp
    )]
    [TestCase(
        DocumentCacheLifecycleReadStatus.Invalid,
        DocumentCacheStatusReason.StateMissingOrInvalid,
        DocumentCacheStatusProcessEligibilityStatus.Ineligible,
        DocumentCacheOperationalHealthStatus.NonOperational,
        DocumentCacheCaughtUpStatus.NotCaughtUp
    )]
    [TestCase(
        DocumentCacheLifecycleReadStatus.Unreadable,
        DocumentCacheStatusReason.ProviderObservationFailed,
        DocumentCacheStatusProcessEligibilityStatus.Unknown,
        DocumentCacheOperationalHealthStatus.Unknown,
        DocumentCacheCaughtUpStatus.Unknown
    )]
    public void It_classifies_lifecycle_read_failures_when_no_higher_priority_process_failure_exists(
        DocumentCacheLifecycleReadStatus lifecycleReadStatus,
        DocumentCacheStatusReason expectedReason,
        DocumentCacheStatusProcessEligibilityStatus expectedEligibilityStatus,
        DocumentCacheOperationalHealthStatus expectedOperationalHealthStatus,
        DocumentCacheCaughtUpStatus expectedCaughtUpStatus
    )
    {
        DocumentCacheStatusClassificationResult result = Classify(
            TargetWithLifecycleReadFailure(lifecycleReadStatus),
            RunningRuntime(),
            DocumentCacheStatusDurableObservation.Success(
                DocumentCacheLifecycleState.Tracking,
                cacheAheadRecoveryRequired: false,
                DocumentCacheStatusQueuePresence.Empty,
                oldestWorkFirstEnqueuedAt: null,
                oldestWorkAgeSeconds: null,
                DurableObservedAt
            )
        );

        result.ProcessEligibility.Status.Should().Be(expectedEligibilityStatus);
        result.ProcessEligibility.Reason.Should().Be(expectedReason);
        result.DurableObservedAt.Should().BeNull();
        result.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unavailable);
        result.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unavailable);
        result.OperationalHealth.Status.Should().Be(expectedOperationalHealthStatus);
        result.OperationalHealth.Reason.Should().Be(expectedReason);
        result.CaughtUp.Status.Should().Be(expectedCaughtUpStatus);
        result.CaughtUp.Reason.Should().Be(expectedReason);
    }

    [TestCaseSource(nameof(LifecycleWithHigherPriorityDiagnosticCases))]
    public void It_applies_process_reason_precedence_before_lifecycle_observation_diagnostics(
        DocumentCacheStatusReason expectedReason
    )
    {
        DocumentCacheStatusClassificationResult result = Classify(
            TargetWithLifecycleAndHigherPriorityFailure(
                expectedReason,
                includeHigherPriorityDiagnostic: true
            ),
            RunningRuntime(),
            durableObservation: null
        );

        result.ProcessEligibility.Status.Should().Be(DocumentCacheStatusProcessEligibilityStatus.Ineligible);
        result.ProcessEligibility.Reason.Should().Be(expectedReason);
        result.DurableObservedAt.Should().BeNull();
        result.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unavailable);
        result.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unavailable);
    }

    [TestCaseSource(nameof(LifecycleWithHigherPriorityDiagnosticCases))]
    public void It_applies_field_failure_precedence_before_lifecycle_read_status(
        DocumentCacheStatusReason expectedReason
    )
    {
        DocumentCacheStatusClassificationResult result = Classify(
            TargetWithLifecycleAndHigherPriorityFailure(
                expectedReason,
                includeHigherPriorityDiagnostic: false
            ),
            RunningRuntime(),
            durableObservation: null
        );

        result.ProcessEligibility.Status.Should().Be(DocumentCacheStatusProcessEligibilityStatus.Ineligible);
        result.ProcessEligibility.Reason.Should().Be(expectedReason);
        result.DurableObservedAt.Should().BeNull();
        result.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unavailable);
        result.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unavailable);
    }

    [Test]
    public void It_classifies_lifecycle_observation_failures_without_state_fact_as_provider_observation_failed()
    {
        DocumentCacheStatusClassificationResult result = Classify(
            TargetWithLifecycleReadFailure(lifecycleReadStatus: null),
            RunningRuntime(),
            durableObservation: null
        );

        result.ProcessEligibility.Status.Should().Be(DocumentCacheStatusProcessEligibilityStatus.Unknown);
        result.ProcessEligibility.Reason.Should().Be(DocumentCacheStatusReason.ProviderObservationFailed);
        result.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Unknown);
        result.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.ProviderObservationFailed);
        result.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.Unknown);
        result.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.ProviderObservationFailed);
        result.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unavailable);
        result.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unavailable);
    }

    [TestCaseSource(nameof(DurableUnknownCases))]
    public void It_uses_unknown_durable_fields_when_current_source_observation_does_not_return_facts(
        DocumentCacheStatusDurableObservation durableObservation,
        DocumentCacheStatusReason expectedReason
    )
    {
        DocumentCacheStatusClassificationResult result = Classify(
            EligibleTarget(),
            RunningRuntime(),
            durableObservation
        );

        result.ProcessEligibility.IsEligible.Should().BeTrue();
        result.DurableObservedAt.Should().BeNull();
        result.Lifecycle.State.Should().Be(DocumentCacheStatusLifecycleState.Unknown);
        result.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unknown);
        result.CacheAhead.State.Should().Be(DocumentCacheStatusCacheAheadState.Unknown);
        result.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unknown);
        result.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Unknown);
        result.OperationalHealth.Reason.Should().Be(expectedReason);
        result.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.Unknown);
        result.CaughtUp.Reason.Should().Be(expectedReason);
    }

    [Test]
    public void It_reports_that_a_durable_observation_is_required_when_process_is_eligible_and_no_durable_observation_is_supplied()
    {
        DocumentCacheStatusClassificationResult result = Classify(
            EligibleTarget(),
            RunningRuntime(),
            durableObservation: null
        );

        result.ProcessEligibility.IsEligible.Should().BeTrue();
        result.DurableObservationRequired.Should().BeTrue();
        result.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Unknown);
        result.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.ProviderObservationFailed);
    }

    [Test]
    public void It_keeps_cancelling_runtime_eligible_for_durable_observation()
    {
        DocumentCacheStatusClassificationResult result = Classify(
            EligibleTarget(),
            new DocumentCacheStatusRuntimeObservation(
                DocumentCacheStatusExecutionState.Cancelling,
                ProcessObservedAt,
                message: "Runtime cancellation is in flight."
            ),
            DocumentCacheStatusDurableObservation.Success(
                DocumentCacheLifecycleState.Tracking,
                cacheAheadRecoveryRequired: false,
                DocumentCacheStatusQueuePresence.NotEmpty,
                OldestWorkFirstEnqueuedAt,
                oldestWorkAgeSeconds: 300,
                DurableObservedAt
            )
        );

        result.ProcessEligibility.Status.Should().Be(DocumentCacheStatusProcessEligibilityStatus.Eligible);
        result.DurableObservedAt.Should().Be(DurableObservedAt);
        result.Lifecycle.State.Should().Be(DocumentCacheStatusLifecycleState.Tracking);
        result.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Available);
        result.CacheAhead.State.Should().Be(DocumentCacheStatusCacheAheadState.Clear);
        result.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.NotEmpty);
        result.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Operational);
        result.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.None);
        result.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.NotCaughtUp);
        result.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.QueueNotEmpty);
    }

    [Test]
    public void It_classifies_cancelled_runtime_as_process_failure()
    {
        DocumentCacheStatusClassificationResult result = Classify(
            EligibleTarget(),
            new DocumentCacheStatusRuntimeObservation(
                DocumentCacheStatusExecutionState.Cancelled,
                ProcessObservedAt,
                message: "Runtime cancellation completed."
            ),
            DocumentCacheStatusDurableObservation.Success(
                DocumentCacheLifecycleState.Tracking,
                cacheAheadRecoveryRequired: false,
                DocumentCacheStatusQueuePresence.Empty,
                oldestWorkFirstEnqueuedAt: null,
                oldestWorkAgeSeconds: null,
                DurableObservedAt
            )
        );

        result.ProcessEligibility.Status.Should().Be(DocumentCacheStatusProcessEligibilityStatus.Ineligible);
        result.ProcessEligibility.Reason.Should().Be(DocumentCacheStatusReason.RuntimeCancelled);
        result.DurableObservedAt.Should().BeNull();
        result.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unavailable);
        result.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unavailable);
        result.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.NonOperational);
        result.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.RuntimeCancelled);
        result.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.NotCaughtUp);
        result.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.RuntimeCancelled);
    }

    [TestCaseSource(nameof(ProcessPrecedenceCases))]
    public void It_applies_process_reason_precedence(DocumentCacheStatusReason expectedReason)
    {
        DocumentCacheStatusReason[] activeReasons = ProcessPrecedence
            .Skip(Array.IndexOf(ProcessPrecedence, expectedReason))
            .ToArray();

        DocumentCacheStatusProcessEligibility processEligibility =
            DocumentCacheStatusClassifier.ClassifyProcessEligibility(
                TargetWithFailures(activeReasons),
                RuntimeForFailures(activeReasons)
            );

        processEligibility.Reason.Should().Be(expectedReason);
        processEligibility.Status.Should().Be(ExpectedProcessEligibilityStatus(expectedReason));
    }

    [Test]
    public void It_reports_an_unexpected_provider_failure_before_the_missing_fingerprint_fallback()
    {
        DocumentCacheTargetContextGeneration generation = new(3);
        const string message = "DocumentCache provider services do not match this DMS process provider.";
        DocumentCacheTargetDiagnostic diagnostic = Diagnostic(
            DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure,
            generation,
            RelationalProviderToken.Postgresql,
            physicalSourceFingerprint: null,
            inventory: null,
            enqueueTrigger: null,
            sqlServerPrerequisites: null,
            message
        );
        DocumentCacheTargetObservation targetObservation = DocumentCacheTargetObservation.ResolvedIneligible(
            TargetKey,
            EffectiveSettings,
            generation,
            RelationalProviderToken.Postgresql,
            physicalSourceFingerprint: null,
            lifecycle: null,
            inventory: null,
            enqueueTrigger: null,
            sqlServerPrerequisites: null,
            retryState: null,
            [diagnostic]
        );

        DocumentCacheStatusProcessEligibility processEligibility =
            DocumentCacheStatusClassifier.ClassifyProcessEligibility(targetObservation, RunningRuntime());

        processEligibility.Status.Should().Be(DocumentCacheStatusProcessEligibilityStatus.Ineligible);
        processEligibility.Reason.Should().Be(DocumentCacheStatusReason.ProviderObservationFailed);
        processEligibility.Message.Should().Be(message);
    }

    private static IEnumerable<TestCaseData> DurableUnknownCases()
    {
        yield return new TestCaseData(
            DocumentCacheStatusDurableObservation.EndpointTimeout("Endpoint budget expired."),
            DocumentCacheStatusReason.StatusEndpointTimeout
        ).SetName("EndpointTimeout");
        yield return new TestCaseData(
            DocumentCacheStatusDurableObservation.ObservationTimeout("Per-target timeout expired."),
            DocumentCacheStatusReason.StatusObservationTimeout
        ).SetName("ObservationTimeout");
        yield return new TestCaseData(
            DocumentCacheStatusDurableObservation.ProviderObservationFailed("Provider statement failed."),
            DocumentCacheStatusReason.ProviderObservationFailed
        ).SetName("ProviderObservationFailed");
    }

    private static IEnumerable<TestCaseData> ProcessPrecedenceCases() =>
        ProcessPrecedence.Select(reason => new TestCaseData(reason).SetName(reason.ToString()));

    private static IEnumerable<TestCaseData> LifecycleWithHigherPriorityDiagnosticCases()
    {
        yield return new TestCaseData(DocumentCacheStatusReason.InventoryInvalid).SetName("InventoryFailure");
        yield return new TestCaseData(DocumentCacheStatusReason.EnqueueTriggerUnavailable).SetName(
            "EnqueueTriggerFailure"
        );
        yield return new TestCaseData(DocumentCacheStatusReason.SqlServerPrerequisiteFailed).SetName(
            "ProviderPrerequisiteFailed"
        );
        yield return new TestCaseData(DocumentCacheStatusReason.UnsupportedPrerequisiteIncident).SetName(
            "UnsupportedPrerequisiteIncident"
        );
    }

    private static DocumentCacheStatusReason[] ProcessPrecedence =>
        [
            DocumentCacheStatusReason.UnresolvedTarget,
            DocumentCacheStatusReason.ProviderMetadataMissing,
            DocumentCacheStatusReason.ProviderMetadataUnknown,
            DocumentCacheStatusReason.ProviderMismatch,
            DocumentCacheStatusReason.ConnectionInputMissing,
            DocumentCacheStatusReason.PhysicalSourceFingerprintFailure,
            DocumentCacheStatusReason.EffectiveSchemaCompatibilityFailure,
            DocumentCacheStatusReason.ResourceKeyCompatibilityFailure,
            DocumentCacheStatusReason.InventoryInvalid,
            DocumentCacheStatusReason.EnqueueTriggerUnavailable,
            DocumentCacheStatusReason.SqlServerPrerequisiteFailed,
            DocumentCacheStatusReason.UnsupportedPrerequisiteIncident,
            DocumentCacheStatusReason.RuntimeNotObserved,
            DocumentCacheStatusReason.RuntimeCancelled,
            DocumentCacheStatusReason.TargetBackoff,
        ];

    private static DocumentCacheStatusClassificationResult Classify(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheStatusRuntimeObservation? runtimeObservation,
        DocumentCacheStatusDurableObservation? durableObservation
    ) => DocumentCacheStatusClassifier.Classify(targetObservation, runtimeObservation, durableObservation);

    private static DocumentCacheTargetObservation EligibleTarget() =>
        DocumentCacheTargetObservation.ResolvedEligible(
            TargetKey,
            EffectiveSettings,
            new DocumentCacheTargetContextGeneration(3),
            RelationalProviderToken.Postgresql,
            Fingerprint,
            TrackingLifecycle,
            SatisfiedInventory,
            SatisfiedEnqueueTrigger,
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );

    private static DocumentCacheTargetObservation TargetWithLifecycleReadFailure(
        DocumentCacheLifecycleReadStatus? lifecycleReadStatus
    )
    {
        DocumentCacheTargetContextGeneration generation = new(3);
        DocumentCacheInventoryValidationResult inventory = SatisfiedInventory;
        DocumentCacheEnqueueTriggerValidationResult enqueueTrigger = SatisfiedEnqueueTrigger;
        DocumentCacheSqlServerPrerequisiteDetails prerequisites =
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable();
        List<DocumentCacheTargetDiagnostic> diagnostics =
        [
            new(
                TargetKey,
                DocumentCacheTargetResolutionState.Resolved,
                RelationalProviderToken.Postgresql,
                generation,
                Fingerprint,
                lifecycle: null,
                inventory,
                enqueueTrigger,
                sqlServerPrerequisites: prerequisites,
                retryState: null,
                category: DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure,
                message: "Lifecycle read failed."
            ),
        ];

        return DocumentCacheTargetObservation.ResolvedIneligible(
            TargetKey,
            EffectiveSettings,
            generation,
            RelationalProviderToken.Postgresql,
            Fingerprint,
            lifecycle: null,
            inventory,
            enqueueTrigger,
            sqlServerPrerequisites: prerequisites,
            retryState: null,
            diagnostics,
            lifecycleReadStatus: lifecycleReadStatus
        );
    }

    private static DocumentCacheTargetObservation TargetWithLifecycleAndHigherPriorityFailure(
        DocumentCacheStatusReason higherPriorityReason,
        bool includeHigherPriorityDiagnostic
    )
    {
        DocumentCacheTargetContextGeneration generation = new(3);
        DocumentCacheInventoryValidationResult? inventory = SatisfiedInventory;
        DocumentCacheEnqueueTriggerValidationResult? enqueueTrigger = SatisfiedEnqueueTrigger;
        DocumentCacheSqlServerPrerequisiteDetails? sqlServerPrerequisites =
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable();
        List<DocumentCacheTargetDiagnostic> diagnostics =
        [
            Diagnostic(
                DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure,
                generation,
                RelationalProviderToken.Postgresql,
                Fingerprint,
                inventory,
                enqueueTrigger,
                sqlServerPrerequisites,
                "Lifecycle read failed."
            ),
        ];

        void AddHigherPriorityDiagnostic(DocumentCacheTargetDiagnosticCategory category, string message)
        {
            if (!includeHigherPriorityDiagnostic)
            {
                return;
            }

            diagnostics.Add(
                Diagnostic(
                    category,
                    generation,
                    RelationalProviderToken.Postgresql,
                    Fingerprint,
                    inventory,
                    enqueueTrigger,
                    sqlServerPrerequisites,
                    message
                )
            );
        }

        switch (higherPriorityReason)
        {
            case DocumentCacheStatusReason.InventoryInvalid:
                inventory = new DocumentCacheInventoryValidationResult(
                    DocumentCacheInventoryStatus.Invalid,
                    "Inventory invalid."
                );
                AddHigherPriorityDiagnostic(
                    DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                    "Inventory invalid."
                );
                break;
            case DocumentCacheStatusReason.EnqueueTriggerUnavailable:
                enqueueTrigger = new DocumentCacheEnqueueTriggerValidationResult(
                    DocumentCacheEnqueueTriggerStatus.Disabled,
                    "Enqueue trigger disabled."
                );
                AddHigherPriorityDiagnostic(
                    DocumentCacheTargetDiagnosticCategory.EnqueueTriggerFailure,
                    "Enqueue trigger disabled."
                );
                break;
            case DocumentCacheStatusReason.SqlServerPrerequisiteFailed:
                sqlServerPrerequisites = SqlServerPrerequisites(
                    DocumentCacheProviderPrerequisiteStatus.Disabled,
                    DocumentCacheProviderPrerequisiteStatus.Satisfied
                );
                AddHigherPriorityDiagnostic(
                    DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed,
                    "SQL Server prerequisite failed."
                );
                break;
            case DocumentCacheStatusReason.UnsupportedPrerequisiteIncident:
                sqlServerPrerequisites = SqlServerPrerequisites(
                    DocumentCacheProviderPrerequisiteStatus.Unreadable,
                    DocumentCacheProviderPrerequisiteStatus.Unreadable
                );
                AddHigherPriorityDiagnostic(
                    DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident,
                    "SQL Server prerequisite incident."
                );
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(higherPriorityReason),
                    higherPriorityReason,
                    "Unsupported higher-priority lifecycle failure case."
                );
        }

        return DocumentCacheTargetObservation.ResolvedIneligible(
            TargetKey,
            EffectiveSettings,
            generation,
            RelationalProviderToken.Postgresql,
            Fingerprint,
            lifecycle: null,
            inventory,
            enqueueTrigger,
            sqlServerPrerequisites,
            retryState: null,
            diagnostics,
            lifecycleReadStatus: DocumentCacheLifecycleReadStatus.Missing
        );
    }

    private static DocumentCacheTargetObservation TargetWithFailures(
        IReadOnlyCollection<DocumentCacheStatusReason> reasons
    )
    {
        if (reasons.Contains(DocumentCacheStatusReason.UnresolvedTarget))
        {
            DocumentCacheResolutionRetryState retryState = new(
                attemptCount: 1,
                lastAttemptedAt: ProcessObservedAt.AddSeconds(-30),
                nextRetryAt: ProcessObservedAt.AddSeconds(30),
                lastFailureCategory: DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
                lastFailureMessage: "Configured target was not present in CMS."
            );

            DocumentCacheTargetDiagnostic diagnostic = Diagnostic(
                DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
                generation: null,
                providerToken: null,
                physicalSourceFingerprint: null,
                inventory: null,
                enqueueTrigger: null,
                sqlServerPrerequisites: null,
                "Configured target was not present in CMS."
            );

            return DocumentCacheTargetObservation.Unresolved(
                TargetKey,
                EffectiveSettings,
                retryState,
                [diagnostic]
            );
        }

        List<DocumentCacheTargetDiagnostic> diagnostics = [];
        DocumentCacheTargetContextGeneration generation = new(3);
        RelationalProviderToken? providerToken = RelationalProviderToken.Postgresql;
        DocumentCachePhysicalSourceFingerprint? fingerprint = Fingerprint;
        DocumentCacheInventoryValidationResult? inventory = SatisfiedInventory;
        DocumentCacheEnqueueTriggerValidationResult? enqueueTrigger = SatisfiedEnqueueTrigger;
        DocumentCacheSqlServerPrerequisiteDetails? sqlServerPrerequisites =
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable();

        foreach (DocumentCacheStatusReason reason in reasons)
        {
            switch (reason)
            {
                case DocumentCacheStatusReason.ProviderMetadataMissing:
                    providerToken = null;
                    diagnostics.Add(
                        Diagnostic(
                            DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing,
                            generation,
                            providerToken,
                            fingerprint,
                            inventory,
                            enqueueTrigger,
                            sqlServerPrerequisites,
                            "Provider metadata missing."
                        )
                    );
                    break;
                case DocumentCacheStatusReason.ProviderMetadataUnknown:
                    diagnostics.Add(
                        Diagnostic(
                            DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown,
                            generation,
                            providerToken,
                            fingerprint,
                            inventory,
                            enqueueTrigger,
                            sqlServerPrerequisites,
                            "Provider metadata unknown."
                        )
                    );
                    break;
                case DocumentCacheStatusReason.ProviderMismatch:
                    diagnostics.Add(
                        Diagnostic(
                            DocumentCacheTargetDiagnosticCategory.ProviderMismatch,
                            generation,
                            providerToken,
                            fingerprint,
                            inventory,
                            enqueueTrigger,
                            sqlServerPrerequisites,
                            "Provider mismatch."
                        )
                    );
                    break;
                case DocumentCacheStatusReason.ConnectionInputMissing:
                    diagnostics.Add(
                        Diagnostic(
                            DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing,
                            generation,
                            providerToken,
                            fingerprint,
                            inventory,
                            enqueueTrigger,
                            sqlServerPrerequisites,
                            "Connection input missing."
                        )
                    );
                    break;
                case DocumentCacheStatusReason.PhysicalSourceFingerprintFailure:
                    fingerprint = null;
                    diagnostics.Add(
                        Diagnostic(
                            DocumentCacheTargetDiagnosticCategory.PhysicalSourceFingerprintFailure,
                            generation,
                            providerToken,
                            fingerprint,
                            inventory,
                            enqueueTrigger,
                            sqlServerPrerequisites,
                            "Fingerprint failed."
                        )
                    );
                    break;
                case DocumentCacheStatusReason.EffectiveSchemaCompatibilityFailure:
                    diagnostics.Add(
                        Diagnostic(
                            DocumentCacheTargetDiagnosticCategory.EffectiveSchemaCompatibilityFailure,
                            generation,
                            providerToken,
                            fingerprint,
                            inventory,
                            enqueueTrigger,
                            sqlServerPrerequisites,
                            "Effective schema mismatch."
                        )
                    );
                    break;
                case DocumentCacheStatusReason.ResourceKeyCompatibilityFailure:
                    diagnostics.Add(
                        Diagnostic(
                            DocumentCacheTargetDiagnosticCategory.ResourceKeyCompatibilityFailure,
                            generation,
                            providerToken,
                            fingerprint,
                            inventory,
                            enqueueTrigger,
                            sqlServerPrerequisites,
                            "Resource keys mismatch."
                        )
                    );
                    break;
                case DocumentCacheStatusReason.InventoryInvalid:
                    inventory = new DocumentCacheInventoryValidationResult(
                        DocumentCacheInventoryStatus.Invalid,
                        "Inventory invalid."
                    );
                    diagnostics.Add(
                        Diagnostic(
                            DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                            generation,
                            providerToken,
                            fingerprint,
                            inventory,
                            enqueueTrigger,
                            sqlServerPrerequisites,
                            "Inventory invalid."
                        )
                    );
                    break;
                case DocumentCacheStatusReason.EnqueueTriggerUnavailable:
                    enqueueTrigger = new DocumentCacheEnqueueTriggerValidationResult(
                        DocumentCacheEnqueueTriggerStatus.Disabled,
                        "Enqueue trigger disabled."
                    );
                    diagnostics.Add(
                        Diagnostic(
                            DocumentCacheTargetDiagnosticCategory.EnqueueTriggerFailure,
                            generation,
                            providerToken,
                            fingerprint,
                            inventory,
                            enqueueTrigger,
                            sqlServerPrerequisites,
                            "Enqueue trigger disabled."
                        )
                    );
                    break;
                case DocumentCacheStatusReason.SqlServerPrerequisiteFailed:
                    sqlServerPrerequisites = SqlServerPrerequisites(
                        DocumentCacheProviderPrerequisiteStatus.Disabled,
                        DocumentCacheProviderPrerequisiteStatus.Satisfied
                    );
                    diagnostics.Add(
                        Diagnostic(
                            DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed,
                            generation,
                            providerToken,
                            fingerprint,
                            inventory,
                            enqueueTrigger,
                            sqlServerPrerequisites,
                            "SQL Server prerequisite failed."
                        )
                    );
                    break;
                case DocumentCacheStatusReason.UnsupportedPrerequisiteIncident:
                    sqlServerPrerequisites = SqlServerPrerequisites(
                        DocumentCacheProviderPrerequisiteStatus.Unreadable,
                        DocumentCacheProviderPrerequisiteStatus.Unreadable
                    );
                    diagnostics.Add(
                        Diagnostic(
                            DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident,
                            generation,
                            providerToken,
                            fingerprint,
                            inventory,
                            enqueueTrigger,
                            sqlServerPrerequisites,
                            "SQL Server prerequisite incident."
                        )
                    );
                    break;
            }
        }

        return reasons.Any(IsPreRuntimeFailure)
            ? DocumentCacheTargetObservation.ResolvedIneligible(
                TargetKey,
                EffectiveSettings,
                generation,
                providerToken,
                fingerprint,
                TrackingLifecycle,
                inventory,
                enqueueTrigger,
                sqlServerPrerequisites,
                retryState: null,
                diagnostics
            )
            : EligibleTarget();
    }

    private static bool IsPreRuntimeFailure(DocumentCacheStatusReason reason) =>
        reason
            is DocumentCacheStatusReason.ProviderMetadataMissing
                or DocumentCacheStatusReason.ProviderMetadataUnknown
                or DocumentCacheStatusReason.ProviderMismatch
                or DocumentCacheStatusReason.ConnectionInputMissing
                or DocumentCacheStatusReason.PhysicalSourceFingerprintFailure
                or DocumentCacheStatusReason.EffectiveSchemaCompatibilityFailure
                or DocumentCacheStatusReason.ResourceKeyCompatibilityFailure
                or DocumentCacheStatusReason.InventoryInvalid
                or DocumentCacheStatusReason.EnqueueTriggerUnavailable
                or DocumentCacheStatusReason.SqlServerPrerequisiteFailed
                or DocumentCacheStatusReason.UnsupportedPrerequisiteIncident;

    private static DocumentCacheTargetDiagnostic Diagnostic(
        DocumentCacheTargetDiagnosticCategory category,
        DocumentCacheTargetContextGeneration? generation,
        RelationalProviderToken? providerToken,
        DocumentCachePhysicalSourceFingerprint? physicalSourceFingerprint,
        DocumentCacheInventoryValidationResult? inventory,
        DocumentCacheEnqueueTriggerValidationResult? enqueueTrigger,
        DocumentCacheSqlServerPrerequisiteDetails? sqlServerPrerequisites,
        string message
    ) =>
        new(
            TargetKey,
            generation is null
                ? DocumentCacheTargetResolutionState.Unresolved
                : DocumentCacheTargetResolutionState.Resolved,
            providerToken,
            generation,
            physicalSourceFingerprint,
            TrackingLifecycle,
            inventory,
            enqueueTrigger,
            sqlServerPrerequisites,
            retryState: null,
            category,
            message
        );

    private static DocumentCacheSqlServerPrerequisiteDetails SqlServerPrerequisites(
        DocumentCacheProviderPrerequisiteStatus readCommittedSnapshotStatus,
        DocumentCacheProviderPrerequisiteStatus nestedTriggersStatus
    ) =>
        new(
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                readCommittedSnapshotStatus,
                "Read committed snapshot."
            ),
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                nestedTriggersStatus,
                "Nested triggers."
            )
        );

    private static DocumentCacheStatusRuntimeObservation? RuntimeForFailures(
        IReadOnlyCollection<DocumentCacheStatusReason> reasons
    )
    {
        if (reasons.Contains(DocumentCacheStatusReason.RuntimeNotObserved))
        {
            return null;
        }

        if (reasons.Contains(DocumentCacheStatusReason.RuntimeCancelled))
        {
            return new DocumentCacheStatusRuntimeObservation(
                DocumentCacheStatusExecutionState.Cancelled,
                ProcessObservedAt,
                message: "Runtime cancelled."
            );
        }

        if (reasons.Contains(DocumentCacheStatusReason.TargetBackoff))
        {
            return new DocumentCacheStatusRuntimeObservation(
                DocumentCacheStatusExecutionState.TargetBackoff,
                ProcessObservedAt,
                targetBackoffUntil: ProcessObservedAt.AddSeconds(30),
                message: "Target backoff."
            );
        }

        return RunningRuntime();
    }

    private static DocumentCacheStatusRuntimeObservation RunningRuntime() =>
        new(DocumentCacheStatusExecutionState.WaitingForPoll, ProcessObservedAt);

    private static DocumentCacheStatusProcessEligibilityStatus ExpectedProcessEligibilityStatus(
        DocumentCacheStatusReason reason
    ) =>
        reason == DocumentCacheStatusReason.RuntimeNotObserved
            ? DocumentCacheStatusProcessEligibilityStatus.Unknown
            : DocumentCacheStatusProcessEligibilityStatus.Ineligible;
}
