// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.DocumentCache;

public enum DocumentCacheStatusProcessEligibilityStatus
{
    Eligible,
    Ineligible,
    Unknown,
}

public sealed record DocumentCacheStatusRuntimeObservation
{
    public DocumentCacheStatusRuntimeObservation(
        DocumentCacheStatusExecutionState status,
        DateTimeOffset observedAt,
        DateTimeOffset? targetBackoffUntil = null,
        string? message = null
    )
    {
        Status = RequireDefined(status, nameof(status));
        ObservedAt = observedAt.ToUniversalTime();
        TargetBackoffUntil = targetBackoffUntil?.ToUniversalTime();
        Message = SanitizeNullable(message);
    }

    public DocumentCacheStatusExecutionState Status { get; }

    public DateTimeOffset ObservedAt { get; }

    public DateTimeOffset? TargetBackoffUntil { get; }

    public string? Message { get; }

    private static TEnum RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Unsupported runtime status.");
        }

        return value;
    }

    private static string? SanitizeNullable(string? message)
    {
        string sanitized = DocumentCacheDiagnosticText.Sanitize(message);
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }
}

public enum DocumentCacheStatusDurableObservationOutcome
{
    Succeeded,
    StateMissingOrInvalid,
    StatusEndpointTimeout,
    StatusObservationTimeout,
    ProviderObservationFailed,
}

public sealed record DocumentCacheStatusDurableObservation
{
    private DocumentCacheStatusDurableObservation(
        DocumentCacheStatusDurableObservationOutcome outcome,
        DocumentCacheLifecycleState? lifecycleState,
        bool? cacheAheadRecoveryRequired,
        DocumentCacheStatusQueuePresence? queuePresence,
        DateTimeOffset? oldestWorkFirstEnqueuedAt,
        double? oldestWorkAgeSeconds,
        DateTimeOffset? durableObservedAt,
        string? message
    )
    {
        Outcome = RequireDefined(outcome, nameof(outcome));
        LifecycleState = lifecycleState;
        CacheAheadRecoveryRequired = cacheAheadRecoveryRequired;
        QueuePresence = queuePresence;
        OldestWorkFirstEnqueuedAt = oldestWorkFirstEnqueuedAt?.ToUniversalTime();
        OldestWorkAgeSeconds = oldestWorkAgeSeconds;
        DurableObservedAt = durableObservedAt?.ToUniversalTime();
        Message = SanitizeNullable(message);

        Validate();
    }

    public DocumentCacheStatusDurableObservationOutcome Outcome { get; }

    public DocumentCacheLifecycleState? LifecycleState { get; }

    public bool? CacheAheadRecoveryRequired { get; }

    public DocumentCacheStatusQueuePresence? QueuePresence { get; }

    public DateTimeOffset? OldestWorkFirstEnqueuedAt { get; }

    public double? OldestWorkAgeSeconds { get; }

    public DateTimeOffset? DurableObservedAt { get; }

    public string? Message { get; }

    public static DocumentCacheStatusDurableObservation Success(
        DocumentCacheLifecycleState lifecycleState,
        bool cacheAheadRecoveryRequired,
        DocumentCacheStatusQueuePresence queuePresence,
        DateTimeOffset? oldestWorkFirstEnqueuedAt,
        double? oldestWorkAgeSeconds,
        DateTimeOffset durableObservedAt
    ) =>
        new(
            DocumentCacheStatusDurableObservationOutcome.Succeeded,
            lifecycleState,
            cacheAheadRecoveryRequired,
            queuePresence,
            oldestWorkFirstEnqueuedAt,
            oldestWorkAgeSeconds,
            durableObservedAt,
            message: null
        );

    public static DocumentCacheStatusDurableObservation StateMissingOrInvalid(
        DateTimeOffset durableObservedAt,
        string message
    ) =>
        StateMissingOrInvalid(
            durableObservedAt,
            queuePresence: null,
            oldestWorkFirstEnqueuedAt: null,
            oldestWorkAgeSeconds: null,
            message
        );

    public static DocumentCacheStatusDurableObservation StateMissingOrInvalid(
        DateTimeOffset durableObservedAt,
        DocumentCacheStatusQueuePresence? queuePresence,
        DateTimeOffset? oldestWorkFirstEnqueuedAt,
        double? oldestWorkAgeSeconds,
        string message
    ) =>
        new(
            DocumentCacheStatusDurableObservationOutcome.StateMissingOrInvalid,
            lifecycleState: null,
            cacheAheadRecoveryRequired: null,
            queuePresence,
            oldestWorkFirstEnqueuedAt,
            oldestWorkAgeSeconds,
            durableObservedAt,
            message
        );

    public static DocumentCacheStatusDurableObservation EndpointTimeout(string message) =>
        DurableUnknown(DocumentCacheStatusDurableObservationOutcome.StatusEndpointTimeout, message);

    public static DocumentCacheStatusDurableObservation ObservationTimeout(string message) =>
        DurableUnknown(DocumentCacheStatusDurableObservationOutcome.StatusObservationTimeout, message);

    public static DocumentCacheStatusDurableObservation ProviderObservationFailed(string message) =>
        DurableUnknown(DocumentCacheStatusDurableObservationOutcome.ProviderObservationFailed, message);

    private static DocumentCacheStatusDurableObservation DurableUnknown(
        DocumentCacheStatusDurableObservationOutcome outcome,
        string message
    ) =>
        new(
            outcome,
            lifecycleState: null,
            cacheAheadRecoveryRequired: null,
            queuePresence: null,
            oldestWorkFirstEnqueuedAt: null,
            oldestWorkAgeSeconds: null,
            durableObservedAt: null,
            message
        );

    private void Validate()
    {
        if (OldestWorkAgeSeconds is < 0)
        {
            throw new ArgumentException("Oldest work age must not be negative when supplied.");
        }

        if (Outcome == DocumentCacheStatusDurableObservationOutcome.Succeeded)
        {
            if (
                LifecycleState is null
                || CacheAheadRecoveryRequired is null
                || QueuePresence is null
                || DurableObservedAt is null
            )
            {
                throw new ArgumentException(
                    "Successful durable observations require lifecycle, cache-ahead, queue, and durable timestamp facts."
                );
            }

            if (
                QueuePresence
                is not DocumentCacheStatusQueuePresence.Empty
                    and not DocumentCacheStatusQueuePresence.NotEmpty
            )
            {
                throw new ArgumentException(
                    "Successful durable observations require empty or not-empty queue presence."
                );
            }

            ValidateObservedQueueFacts(queuePresenceRequired: true);

            return;
        }

        if (Outcome == DocumentCacheStatusDurableObservationOutcome.StateMissingOrInvalid)
        {
            if (DurableObservedAt is null)
            {
                throw new ArgumentException(
                    "Missing or invalid state observations require a durable observation timestamp."
                );
            }

            if (LifecycleState is not null || CacheAheadRecoveryRequired is not null)
            {
                throw new ArgumentException(
                    "Missing or invalid state observations must not carry lifecycle or cache-ahead facts."
                );
            }

            ValidateObservedQueueFacts(queuePresenceRequired: false);

            return;
        }

        if (
            LifecycleState is not null
            || CacheAheadRecoveryRequired is not null
            || QueuePresence is not null
            || OldestWorkFirstEnqueuedAt is not null
            || OldestWorkAgeSeconds is not null
            || DurableObservedAt is not null
        )
        {
            throw new ArgumentException("Unknown durable observations must not carry stale durable facts.");
        }
    }

    private void ValidateObservedQueueFacts(bool queuePresenceRequired)
    {
        if (QueuePresence is null)
        {
            if (queuePresenceRequired)
            {
                throw new ArgumentException("Queue observations require queue presence.");
            }

            if (OldestWorkFirstEnqueuedAt is not null || OldestWorkAgeSeconds is not null)
            {
                throw new ArgumentException(
                    "Queue observations without queue presence must not carry oldest-work facts."
                );
            }

            return;
        }

        if (
            QueuePresence
            is not DocumentCacheStatusQueuePresence.Empty
                and not DocumentCacheStatusQueuePresence.NotEmpty
        )
        {
            throw new ArgumentException(
                "Durable queue observations require empty or not-empty queue presence."
            );
        }

        if (QueuePresence == DocumentCacheStatusQueuePresence.Empty)
        {
            if (OldestWorkFirstEnqueuedAt is not null || OldestWorkAgeSeconds is not null)
            {
                throw new ArgumentException("Empty queue observations must not carry oldest-work facts.");
            }
        }
        else if (OldestWorkFirstEnqueuedAt is null || OldestWorkAgeSeconds is null)
        {
            throw new ArgumentException("Non-empty queue observations require oldest-work facts.");
        }
    }

    private static TEnum RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Unsupported durable outcome.");
        }

        return value;
    }

    private static string? SanitizeNullable(string? message)
    {
        string sanitized = DocumentCacheDiagnosticText.Sanitize(message);
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }
}

public sealed record DocumentCacheStatusProcessEligibility
{
    private DocumentCacheStatusProcessEligibility(
        DocumentCacheStatusProcessEligibilityStatus status,
        DocumentCacheStatusReason reason,
        string? message
    )
    {
        Status = status;
        Reason = reason;
        Message = SanitizeNullable(message);
    }

    public DocumentCacheStatusProcessEligibilityStatus Status { get; }

    public DocumentCacheStatusReason Reason { get; }

    public string? Message { get; }

    public bool IsEligible => Status == DocumentCacheStatusProcessEligibilityStatus.Eligible;

    public static DocumentCacheStatusProcessEligibility Eligible() =>
        new(
            DocumentCacheStatusProcessEligibilityStatus.Eligible,
            DocumentCacheStatusReason.None,
            message: null
        );

    public static DocumentCacheStatusProcessEligibility Ineligible(
        DocumentCacheStatusReason reason,
        string? message
    ) => new(DocumentCacheStatusProcessEligibilityStatus.Ineligible, reason, message);

    public static DocumentCacheStatusProcessEligibility Unknown(
        DocumentCacheStatusReason reason,
        string? message
    ) => new(DocumentCacheStatusProcessEligibilityStatus.Unknown, reason, message);

    private static string? SanitizeNullable(string? message)
    {
        string sanitized = DocumentCacheDiagnosticText.Sanitize(message);
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }
}

public sealed record DocumentCacheStatusClassificationResult(
    DocumentCacheStatusProcessEligibility ProcessEligibility,
    bool DurableObservationRequired,
    DateTimeOffset? DurableObservedAt,
    DocumentCacheStatusLifecycleComponent Lifecycle,
    DocumentCacheStatusCacheAheadComponent CacheAhead,
    DocumentCacheStatusQueueSummary QueueSummary,
    DocumentCacheOperationalHealthComponent OperationalHealth,
    DocumentCacheCaughtUpComponent CaughtUp
);

public static class DocumentCacheStatusClassifier
{
    private static readonly (
        DocumentCacheTargetDiagnosticCategory Category,
        DocumentCacheStatusReason Reason
    )[] ProcessDiagnosticPrecedence =
    [
        (
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing,
            DocumentCacheStatusReason.ProviderMetadataMissing
        ),
        (
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown,
            DocumentCacheStatusReason.ProviderMetadataUnknown
        ),
        (DocumentCacheTargetDiagnosticCategory.ProviderMismatch, DocumentCacheStatusReason.ProviderMismatch),
        (
            DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing,
            DocumentCacheStatusReason.ConnectionInputMissing
        ),
        (
            DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure,
            DocumentCacheStatusReason.ProviderObservationFailed
        ),
        (
            DocumentCacheTargetDiagnosticCategory.PhysicalSourceFingerprintFailure,
            DocumentCacheStatusReason.PhysicalSourceFingerprintFailure
        ),
        (
            DocumentCacheTargetDiagnosticCategory.EffectiveSchemaCompatibilityFailure,
            DocumentCacheStatusReason.EffectiveSchemaCompatibilityFailure
        ),
        (
            DocumentCacheTargetDiagnosticCategory.ResourceKeyCompatibilityFailure,
            DocumentCacheStatusReason.ResourceKeyCompatibilityFailure
        ),
        (DocumentCacheTargetDiagnosticCategory.InventoryFailure, DocumentCacheStatusReason.InventoryInvalid),
        (
            DocumentCacheTargetDiagnosticCategory.EnqueueTriggerFailure,
            DocumentCacheStatusReason.EnqueueTriggerUnavailable
        ),
        (
            DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed,
            DocumentCacheStatusReason.SqlServerPrerequisiteFailed
        ),
        (
            DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident,
            DocumentCacheStatusReason.UnsupportedPrerequisiteIncident
        ),
    ];

    public static DocumentCacheStatusProcessEligibility ClassifyProcessEligibility(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheStatusRuntimeObservation? runtimeObservation
    )
    {
        ArgumentNullException.ThrowIfNull(targetObservation);

        if (targetObservation.ResolutionState != DocumentCacheTargetResolutionState.Resolved)
        {
            string? message =
                LatestDiagnosticMessage(targetObservation)
                ?? targetObservation.RetryState?.LastFailureMessage
                ?? "DocumentCache target is not currently resolved.";

            return targetObservation.ResolutionState == DocumentCacheTargetResolutionState.Configured
                ? DocumentCacheStatusProcessEligibility.Unknown(
                    DocumentCacheStatusReason.UnresolvedTarget,
                    message
                )
                : DocumentCacheStatusProcessEligibility.Ineligible(
                    DocumentCacheStatusReason.UnresolvedTarget,
                    message
                );
        }

        foreach (
            (
                DocumentCacheTargetDiagnosticCategory category,
                DocumentCacheStatusReason reason
            ) in ProcessDiagnosticPrecedence
        )
        {
            DocumentCacheTargetDiagnostic? diagnostic = LatestDiagnostic(targetObservation, category);
            if (diagnostic is not null)
            {
                return DocumentCacheStatusProcessEligibility.Ineligible(reason, diagnostic.Message);
            }
        }

        DocumentCacheStatusProcessEligibility? fieldFailure = SelectFieldFailure(targetObservation);
        if (fieldFailure is not null)
        {
            return fieldFailure;
        }

        if (runtimeObservation is null)
        {
            return DocumentCacheStatusProcessEligibility.Unknown(
                DocumentCacheStatusReason.RuntimeNotObserved,
                "Current-generation DocumentCache projection runtime has not been observed."
            );
        }

        return runtimeObservation.Status switch
        {
            DocumentCacheStatusExecutionState.Cancelled => DocumentCacheStatusProcessEligibility.Ineligible(
                DocumentCacheStatusReason.RuntimeCancelled,
                runtimeObservation.Message
                    ?? "Current-generation DocumentCache projection runtime is cancelled."
            ),
            DocumentCacheStatusExecutionState.TargetBackoff =>
                DocumentCacheStatusProcessEligibility.Ineligible(
                    DocumentCacheStatusReason.TargetBackoff,
                    runtimeObservation.Message
                        ?? "Current-generation DocumentCache projection runtime is in target-level backoff."
                ),
            DocumentCacheStatusExecutionState.NotObserved => DocumentCacheStatusProcessEligibility.Unknown(
                DocumentCacheStatusReason.RuntimeNotObserved,
                runtimeObservation.Message
                    ?? "Current-generation DocumentCache projection runtime has not been observed."
            ),
            _ => DocumentCacheStatusProcessEligibility.Eligible(),
        };
    }

    public static DocumentCacheStatusClassificationResult Classify(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheStatusRuntimeObservation? runtimeObservation,
        DocumentCacheStatusDurableObservation? durableObservation,
        DocumentCacheStatusEvaluationMode evaluationMode = DocumentCacheStatusEvaluationMode.RuntimeEndpoint
    )
    {
        if (!Enum.IsDefined(evaluationMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(evaluationMode),
                evaluationMode,
                "Unsupported status evaluation mode."
            );
        }

        DocumentCacheStatusProcessEligibility processEligibility = ClassifyProcessEligibility(
            targetObservation,
            runtimeObservation
        );
        bool standaloneRuntimeNotObserved =
            evaluationMode == DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
            && IsRuntimeNotObserved(processEligibility);

        if (!processEligibility.IsEligible && !standaloneRuntimeNotObserved)
        {
            return FromProcessFailure(processEligibility);
        }

        if (durableObservation is null)
        {
            DocumentCacheStatusClassificationResult durableMissingResult = FromDurableUnknown(
                processEligibility,
                DocumentCacheStatusReason.ProviderObservationFailed,
                "DocumentCache durable status observation was not supplied."
            ) with
            {
                DurableObservationRequired = true,
            };

            return standaloneRuntimeNotObserved
                ? WithStandaloneRuntimeNotObservedHealth(durableMissingResult, processEligibility)
                : durableMissingResult;
        }

        DocumentCacheStatusClassificationResult result = durableObservation.Outcome switch
        {
            DocumentCacheStatusDurableObservationOutcome.StatusEndpointTimeout => FromDurableUnknown(
                processEligibility,
                DocumentCacheStatusReason.StatusEndpointTimeout,
                durableObservation.Message
            ),
            DocumentCacheStatusDurableObservationOutcome.StatusObservationTimeout => FromDurableUnknown(
                processEligibility,
                DocumentCacheStatusReason.StatusObservationTimeout,
                durableObservation.Message
            ),
            DocumentCacheStatusDurableObservationOutcome.ProviderObservationFailed => FromDurableUnknown(
                processEligibility,
                DocumentCacheStatusReason.ProviderObservationFailed,
                durableObservation.Message
            ),
            DocumentCacheStatusDurableObservationOutcome.StateMissingOrInvalid => FromStateMissingOrInvalid(
                processEligibility,
                durableObservation
            ),
            DocumentCacheStatusDurableObservationOutcome.Succeeded => FromDurableSuccess(
                processEligibility,
                durableObservation
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(durableObservation),
                durableObservation.Outcome,
                "Unsupported durable observation outcome."
            ),
        };

        return standaloneRuntimeNotObserved && !ShouldPreserveStandaloneDurableTimeoutReason(result)
            ? WithStandaloneRuntimeNotObservedHealth(result, processEligibility)
            : result;
    }

    private static bool IsRuntimeNotObserved(DocumentCacheStatusProcessEligibility processEligibility) =>
        processEligibility.Status == DocumentCacheStatusProcessEligibilityStatus.Unknown
        && processEligibility.Reason == DocumentCacheStatusReason.RuntimeNotObserved;

    private static bool ShouldPreserveStandaloneDurableTimeoutReason(
        DocumentCacheStatusClassificationResult result
    ) =>
        result.OperationalHealth.Reason
            is DocumentCacheStatusReason.StatusEndpointTimeout
                or DocumentCacheStatusReason.StatusObservationTimeout;

    private static DocumentCacheStatusClassificationResult WithStandaloneRuntimeNotObservedHealth(
        DocumentCacheStatusClassificationResult result,
        DocumentCacheStatusProcessEligibility processEligibility
    ) =>
        result with
        {
            OperationalHealth = new DocumentCacheOperationalHealthComponent(
                DocumentCacheOperationalHealthStatus.Unknown,
                DocumentCacheStatusReason.RuntimeNotObserved,
                processEligibility.Message
            ),
            CaughtUp = new DocumentCacheCaughtUpComponent(
                DocumentCacheCaughtUpStatus.Unknown,
                DocumentCacheStatusReason.RuntimeNotObserved,
                processEligibility.Message
            ),
        };

    private static DocumentCacheStatusClassificationResult FromProcessFailure(
        DocumentCacheStatusProcessEligibility processEligibility
    ) =>
        new(
            processEligibility,
            DurableObservationRequired: false,
            DurableObservedAt: null,
            new DocumentCacheStatusLifecycleComponent(
                DocumentCacheStatusLifecycleState.Unknown,
                DocumentCacheStatusAvailability.Unavailable,
                message: null
            ),
            new DocumentCacheStatusCacheAheadComponent(
                DocumentCacheStatusCacheAheadState.Unknown,
                recoveryRequired: null,
                message: null
            ),
            QueueUnavailable(),
            ProcessOperationalHealth(processEligibility),
            ProcessCaughtUp(processEligibility)
        );

    private static DocumentCacheStatusClassificationResult FromDurableUnknown(
        DocumentCacheStatusProcessEligibility processEligibility,
        DocumentCacheStatusReason reason,
        string? message
    )
    {
        string? sanitizedMessage = SanitizeNullable(message);
        return new(
            processEligibility,
            DurableObservationRequired: false,
            DurableObservedAt: null,
            new DocumentCacheStatusLifecycleComponent(
                DocumentCacheStatusLifecycleState.Unknown,
                DocumentCacheStatusAvailability.Unknown,
                sanitizedMessage
            ),
            new DocumentCacheStatusCacheAheadComponent(
                DocumentCacheStatusCacheAheadState.Unknown,
                recoveryRequired: null,
                sanitizedMessage
            ),
            QueueUnknown(),
            new DocumentCacheOperationalHealthComponent(
                DocumentCacheOperationalHealthStatus.Unknown,
                reason,
                sanitizedMessage
            ),
            new DocumentCacheCaughtUpComponent(DocumentCacheCaughtUpStatus.Unknown, reason, sanitizedMessage)
        );
    }

    private static DocumentCacheStatusClassificationResult FromStateMissingOrInvalid(
        DocumentCacheStatusProcessEligibility processEligibility,
        DocumentCacheStatusDurableObservation durableObservation
    )
    {
        string? message = durableObservation.Message;
        return new(
            processEligibility,
            DurableObservationRequired: false,
            durableObservation.DurableObservedAt,
            new DocumentCacheStatusLifecycleComponent(
                DocumentCacheStatusLifecycleState.Invalid,
                DocumentCacheStatusAvailability.Available,
                message
            ),
            new DocumentCacheStatusCacheAheadComponent(
                DocumentCacheStatusCacheAheadState.Unknown,
                recoveryRequired: null,
                message
            ),
            durableObservation.QueuePresence is null
                ? QueueUnknown()
                : QueueSummaryFromDurableObservation(durableObservation),
            new DocumentCacheOperationalHealthComponent(
                DocumentCacheOperationalHealthStatus.NonOperational,
                DocumentCacheStatusReason.StateMissingOrInvalid,
                message
            ),
            new DocumentCacheCaughtUpComponent(
                DocumentCacheCaughtUpStatus.NotCaughtUp,
                DocumentCacheStatusReason.StateMissingOrInvalid,
                message
            )
        );
    }

    private static DocumentCacheStatusClassificationResult FromDurableSuccess(
        DocumentCacheStatusProcessEligibility processEligibility,
        DocumentCacheStatusDurableObservation durableObservation
    )
    {
        DocumentCacheStatusLifecycleState lifecycleState = ToStatusLifecycleState(
            durableObservation.LifecycleState!.Value
        );
        bool cacheAheadRecoveryRequired = durableObservation.CacheAheadRecoveryRequired!.Value;
        DocumentCacheStatusReason durableHealthReason = SelectDurableOperationalReason(
            durableObservation.LifecycleState.Value,
            cacheAheadRecoveryRequired
        );
        DocumentCacheOperationalHealthComponent operationalHealth =
            durableHealthReason == DocumentCacheStatusReason.None
                ? new DocumentCacheOperationalHealthComponent(
                    DocumentCacheOperationalHealthStatus.Operational,
                    DocumentCacheStatusReason.None,
                    message: null
                )
                : new DocumentCacheOperationalHealthComponent(
                    DocumentCacheOperationalHealthStatus.NonOperational,
                    durableHealthReason,
                    message: null
                );

        DocumentCacheCaughtUpComponent caughtUp = SelectCaughtUp(durableObservation, durableHealthReason);

        return new(
            processEligibility,
            DurableObservationRequired: false,
            durableObservation.DurableObservedAt,
            new DocumentCacheStatusLifecycleComponent(
                lifecycleState,
                DocumentCacheStatusAvailability.Available,
                message: null
            ),
            new DocumentCacheStatusCacheAheadComponent(
                cacheAheadRecoveryRequired
                    ? DocumentCacheStatusCacheAheadState.RecoveryRequired
                    : DocumentCacheStatusCacheAheadState.Clear,
                cacheAheadRecoveryRequired,
                message: null
            ),
            QueueSummaryFromDurableObservation(durableObservation),
            operationalHealth,
            caughtUp
        );
    }

    private static DocumentCacheOperationalHealthComponent ProcessOperationalHealth(
        DocumentCacheStatusProcessEligibility processEligibility
    ) =>
        processEligibility.Status == DocumentCacheStatusProcessEligibilityStatus.Unknown
            ? new DocumentCacheOperationalHealthComponent(
                DocumentCacheOperationalHealthStatus.Unknown,
                processEligibility.Reason,
                processEligibility.Message
            )
            : new DocumentCacheOperationalHealthComponent(
                DocumentCacheOperationalHealthStatus.NonOperational,
                processEligibility.Reason,
                processEligibility.Message
            );

    private static DocumentCacheCaughtUpComponent ProcessCaughtUp(
        DocumentCacheStatusProcessEligibility processEligibility
    ) =>
        processEligibility.Status == DocumentCacheStatusProcessEligibilityStatus.Unknown
            ? new DocumentCacheCaughtUpComponent(
                DocumentCacheCaughtUpStatus.Unknown,
                processEligibility.Reason,
                processEligibility.Message
            )
            : new DocumentCacheCaughtUpComponent(
                DocumentCacheCaughtUpStatus.NotCaughtUp,
                processEligibility.Reason,
                processEligibility.Message
            );

    private static DocumentCacheCaughtUpComponent SelectCaughtUp(
        DocumentCacheStatusDurableObservation durableObservation,
        DocumentCacheStatusReason durableHealthReason
    )
    {
        if (durableHealthReason != DocumentCacheStatusReason.None)
        {
            return new DocumentCacheCaughtUpComponent(
                DocumentCacheCaughtUpStatus.NotCaughtUp,
                durableHealthReason,
                message: null
            );
        }

        return durableObservation.QueuePresence == DocumentCacheStatusQueuePresence.NotEmpty
            ? new DocumentCacheCaughtUpComponent(
                DocumentCacheCaughtUpStatus.NotCaughtUp,
                DocumentCacheStatusReason.QueueNotEmpty,
                message: null
            )
            : new DocumentCacheCaughtUpComponent(
                DocumentCacheCaughtUpStatus.CaughtUp,
                DocumentCacheStatusReason.None,
                message: null
            );
    }

    private static DocumentCacheStatusReason SelectDurableOperationalReason(
        DocumentCacheLifecycleState lifecycleState,
        bool cacheAheadRecoveryRequired
    ) =>
        lifecycleState switch
        {
            DocumentCacheLifecycleState.Disabled => DocumentCacheStatusReason.LifecycleDisabled,
            DocumentCacheLifecycleState.Resetting => DocumentCacheStatusReason.LifecycleResetting,
            DocumentCacheLifecycleState.Rebuilding => DocumentCacheStatusReason.LifecycleRebuilding,
            DocumentCacheLifecycleState.Tracking when cacheAheadRecoveryRequired =>
                DocumentCacheStatusReason.CacheAheadRecoveryRequired,
            DocumentCacheLifecycleState.Tracking => DocumentCacheStatusReason.None,
            _ => DocumentCacheStatusReason.StateMissingOrInvalid,
        };

    private static DocumentCacheStatusLifecycleState ToStatusLifecycleState(
        DocumentCacheLifecycleState lifecycleState
    ) =>
        lifecycleState switch
        {
            DocumentCacheLifecycleState.Disabled => DocumentCacheStatusLifecycleState.Disabled,
            DocumentCacheLifecycleState.Resetting => DocumentCacheStatusLifecycleState.Resetting,
            DocumentCacheLifecycleState.Rebuilding => DocumentCacheStatusLifecycleState.Rebuilding,
            DocumentCacheLifecycleState.Tracking => DocumentCacheStatusLifecycleState.Tracking,
            _ => DocumentCacheStatusLifecycleState.Invalid,
        };

    private static DocumentCacheStatusQueueSummary QueueUnavailable() =>
        new(
            DocumentCacheStatusQueuePresence.Unavailable,
            oldestWorkFirstEnqueuedAt: null,
            oldestWorkAgeSeconds: null,
            DocumentCacheStatusBacklogEstimate.Unavailable
        );

    private static DocumentCacheStatusQueueSummary QueueUnknown() =>
        new(
            DocumentCacheStatusQueuePresence.Unknown,
            oldestWorkFirstEnqueuedAt: null,
            oldestWorkAgeSeconds: null,
            DocumentCacheStatusBacklogEstimate.Unavailable
        );

    private static DocumentCacheStatusQueueSummary QueueSummaryFromDurableObservation(
        DocumentCacheStatusDurableObservation durableObservation
    ) =>
        new(
            durableObservation.QueuePresence!.Value,
            durableObservation.OldestWorkFirstEnqueuedAt,
            durableObservation.OldestWorkAgeSeconds,
            DocumentCacheStatusBacklogEstimate.Unavailable
        );

    private static DocumentCacheStatusProcessEligibility? SelectFieldFailure(
        DocumentCacheTargetObservation targetObservation
    )
    {
        if (targetObservation.ProviderToken is null)
        {
            return DocumentCacheStatusProcessEligibility.Ineligible(
                DocumentCacheStatusReason.ProviderMetadataMissing,
                "Resolved target is missing relational provider metadata."
            );
        }

        if (targetObservation.PhysicalSourceFingerprint is null)
        {
            return DocumentCacheStatusProcessEligibility.Ineligible(
                DocumentCacheStatusReason.PhysicalSourceFingerprintFailure,
                "Resolved target physical source fingerprint is unavailable."
            );
        }

        if (targetObservation.Inventory is null)
        {
            return DocumentCacheStatusProcessEligibility.Ineligible(
                DocumentCacheStatusReason.InventoryInvalid,
                "DocumentCache inventory has not been observed."
            );
        }

        if (targetObservation.Inventory.Status != DocumentCacheInventoryStatus.Satisfied)
        {
            return DocumentCacheStatusProcessEligibility.Ineligible(
                DocumentCacheStatusReason.InventoryInvalid,
                targetObservation.Inventory.Message
            );
        }

        if (targetObservation.EnqueueTrigger is null)
        {
            return DocumentCacheStatusProcessEligibility.Ineligible(
                DocumentCacheStatusReason.EnqueueTriggerUnavailable,
                "DocumentCache enqueue trigger inventory has not been observed."
            );
        }

        if (targetObservation.EnqueueTrigger.Status != DocumentCacheEnqueueTriggerStatus.Satisfied)
        {
            return DocumentCacheStatusProcessEligibility.Ineligible(
                DocumentCacheStatusReason.EnqueueTriggerUnavailable,
                targetObservation.EnqueueTrigger.Message
            );
        }

        DocumentCacheStatusProcessEligibility? prerequisiteFailure = SelectPrerequisiteFailure(
            targetObservation
        );
        if (prerequisiteFailure is not null)
        {
            return prerequisiteFailure;
        }

        DocumentCacheStatusProcessEligibility? lifecycleFailure = SelectLifecycleObservationFailure(
            targetObservation,
            LatestDiagnostic(
                targetObservation,
                DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure
            )
        );
        if (lifecycleFailure is not null)
        {
            return lifecycleFailure;
        }

        if (targetObservation.EligibilityState != DocumentCacheTargetEligibilityState.Eligible)
        {
            return DocumentCacheStatusProcessEligibility.Ineligible(
                DocumentCacheStatusReason.InventoryInvalid,
                LatestDiagnosticMessage(targetObservation) ?? "DocumentCache target is not eligible."
            );
        }

        return null;
    }

    private static DocumentCacheStatusProcessEligibility? SelectLifecycleObservationFailure(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheTargetDiagnostic? diagnostic
    )
    {
        if (targetObservation.LifecycleReadStatus is null or DocumentCacheLifecycleReadStatus.Succeeded)
        {
            return diagnostic is null
                ? null
                : DocumentCacheStatusProcessEligibility.Unknown(
                    DocumentCacheStatusReason.ProviderObservationFailed,
                    diagnostic.Message
                );
        }

        if (
            targetObservation.LifecycleReadStatus
            is DocumentCacheLifecycleReadStatus.Missing
                or DocumentCacheLifecycleReadStatus.Invalid
        )
        {
            return DocumentCacheStatusProcessEligibility.Ineligible(
                DocumentCacheStatusReason.StateMissingOrInvalid,
                diagnostic?.Message ?? "dms.DocumentCacheState singleton row is missing or invalid."
            );
        }

        return DocumentCacheStatusProcessEligibility.Unknown(
            DocumentCacheStatusReason.ProviderObservationFailed,
            diagnostic?.Message ?? "DocumentCache lifecycle observation failed."
        );
    }

    private static DocumentCacheStatusProcessEligibility? SelectPrerequisiteFailure(
        DocumentCacheTargetObservation targetObservation
    )
    {
        if (targetObservation.SqlServerPrerequisites is null)
        {
            return null;
        }

        DocumentCacheProviderPrerequisiteResult[] failedPrerequisites =
        [
            targetObservation.SqlServerPrerequisites.ReadCommittedSnapshot,
            targetObservation.SqlServerPrerequisites.NestedTriggers,
        ];

        DocumentCacheProviderPrerequisiteResult? failedPrerequisite = Array.Find(
            failedPrerequisites,
            prerequisite => prerequisite.Status == DocumentCacheProviderPrerequisiteStatus.Disabled
        );
        if (failedPrerequisite is not null)
        {
            return DocumentCacheStatusProcessEligibility.Ineligible(
                DocumentCacheStatusReason.SqlServerPrerequisiteFailed,
                failedPrerequisite.Message
            );
        }

        DocumentCacheProviderPrerequisiteResult? unsupportedIncident = Array.Find(
            failedPrerequisites,
            prerequisite => prerequisite.Status == DocumentCacheProviderPrerequisiteStatus.Unreadable
        );
        if (unsupportedIncident is not null)
        {
            return DocumentCacheStatusProcessEligibility.Ineligible(
                DocumentCacheStatusReason.UnsupportedPrerequisiteIncident,
                unsupportedIncident.Message
            );
        }

        return null;
    }

    private static DocumentCacheTargetDiagnostic? LatestDiagnostic(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheTargetDiagnosticCategory category
    ) => targetObservation.Diagnostics.LastOrDefault(diagnostic => diagnostic.Category == category);

    private static string? LatestDiagnosticMessage(DocumentCacheTargetObservation targetObservation) =>
        targetObservation.Diagnostics.Length == 0 ? null : targetObservation.Diagnostics[^1].Message;

    private static string? SanitizeNullable(string? message)
    {
        string sanitized = DocumentCacheDiagnosticText.Sanitize(message);
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }
}
