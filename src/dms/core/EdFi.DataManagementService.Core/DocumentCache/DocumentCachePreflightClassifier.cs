// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Core.DocumentCache;

public sealed record DocumentCacheGuardedNewEmptyActivationState
{
    public DocumentCacheGuardedNewEmptyActivationState(
        bool canonicalDocumentsEmpty,
        bool documentCacheEmpty,
        bool documentProjectionWorkEmpty,
        string message = "Guarded new-empty state observed."
    )
    {
        CanonicalDocumentsEmpty = canonicalDocumentsEmpty;
        DocumentCacheEmpty = documentCacheEmpty;
        DocumentProjectionWorkEmpty = documentProjectionWorkEmpty;
        Message = DocumentCacheDiagnosticText.Sanitize(message);
    }

    public bool CanonicalDocumentsEmpty { get; }

    public bool DocumentCacheEmpty { get; }

    public bool DocumentProjectionWorkEmpty { get; }

    public string Message { get; }

    public bool IsEmpty => CanonicalDocumentsEmpty && DocumentCacheEmpty && DocumentProjectionWorkEmpty;
}

public sealed record DocumentCacheGuardedNewEmptyActivationPreflightFacts
{
    public DocumentCacheGuardedNewEmptyActivationPreflightFacts(
        DocumentCacheTargetContextGeneration? expectedTargetContextGeneration,
        DocumentCacheProviderPrerequisiteValidationResult? activationProviderPrerequisites,
        DocumentCacheGuardedNewEmptyActivationState? guardedNewEmptyState,
        string? unexpectedProviderFailureMessage = null
    )
    {
        ExpectedTargetContextGeneration = expectedTargetContextGeneration;
        ActivationProviderPrerequisites = activationProviderPrerequisites;
        GuardedNewEmptyState = guardedNewEmptyState;
        UnexpectedProviderFailureMessage = DocumentCachePreflightDiagnosticText.SanitizeNullable(
            unexpectedProviderFailureMessage
        );
    }

    public DocumentCacheTargetContextGeneration? ExpectedTargetContextGeneration { get; }

    public DocumentCacheProviderPrerequisiteValidationResult? ActivationProviderPrerequisites { get; }

    public DocumentCacheGuardedNewEmptyActivationState? GuardedNewEmptyState { get; }

    public string? UnexpectedProviderFailureMessage { get; }
}

public sealed record DocumentCacheOfflineActivationPreflightFacts
{
    public DocumentCacheOfflineActivationPreflightFacts(
        DocumentCacheTargetContextGeneration? expectedTargetContextGeneration,
        DocumentCacheProviderPrerequisiteValidationResult? activationProviderPrerequisites,
        DocumentCacheDownstreamPublicationHistoryObservation? downstreamPublicationHistory,
        string? unexpectedProviderFailureMessage = null
    )
    {
        ExpectedTargetContextGeneration = expectedTargetContextGeneration;
        ActivationProviderPrerequisites = activationProviderPrerequisites;
        DownstreamPublicationHistory = downstreamPublicationHistory;
        UnexpectedProviderFailureMessage = DocumentCachePreflightDiagnosticText.SanitizeNullable(
            unexpectedProviderFailureMessage
        );
    }

    public DocumentCacheTargetContextGeneration? ExpectedTargetContextGeneration { get; }

    public DocumentCacheProviderPrerequisiteValidationResult? ActivationProviderPrerequisites { get; }

    public DocumentCacheDownstreamPublicationHistoryObservation? DownstreamPublicationHistory { get; }

    public string? UnexpectedProviderFailureMessage { get; }
}

public sealed record DocumentCacheOfflineDeactivationPreflightFacts
{
    public DocumentCacheOfflineDeactivationPreflightFacts(
        DocumentCacheTargetContextGeneration? expectedTargetContextGeneration,
        DocumentCacheDownstreamPublicationHistoryObservation? downstreamPublicationHistory,
        string? unexpectedProviderFailureMessage = null
    )
    {
        ExpectedTargetContextGeneration = expectedTargetContextGeneration;
        DownstreamPublicationHistory = downstreamPublicationHistory;
        UnexpectedProviderFailureMessage = DocumentCachePreflightDiagnosticText.SanitizeNullable(
            unexpectedProviderFailureMessage
        );
    }

    public DocumentCacheTargetContextGeneration? ExpectedTargetContextGeneration { get; }

    public DocumentCacheDownstreamPublicationHistoryObservation? DownstreamPublicationHistory { get; }

    public string? UnexpectedProviderFailureMessage { get; }
}

public sealed record DocumentCacheOnlineCacheRebuildPreflightFacts
{
    public DocumentCacheOnlineCacheRebuildPreflightFacts(
        DocumentCacheTargetContextGeneration? expectedTargetContextGeneration,
        string? unexpectedProviderFailureMessage = null
    )
    {
        ExpectedTargetContextGeneration = expectedTargetContextGeneration;
        UnexpectedProviderFailureMessage = DocumentCachePreflightDiagnosticText.SanitizeNullable(
            unexpectedProviderFailureMessage
        );
    }

    public DocumentCacheTargetContextGeneration? ExpectedTargetContextGeneration { get; }

    public string? UnexpectedProviderFailureMessage { get; }
}

public sealed record DocumentCacheExplicitIntegrityScrubPreflightFacts
{
    public DocumentCacheExplicitIntegrityScrubPreflightFacts(
        DocumentCacheTargetContextGeneration? expectedTargetContextGeneration,
        string? unexpectedProviderFailureMessage = null
    )
    {
        ExpectedTargetContextGeneration = expectedTargetContextGeneration;
        UnexpectedProviderFailureMessage = DocumentCachePreflightDiagnosticText.SanitizeNullable(
            unexpectedProviderFailureMessage
        );
    }

    public DocumentCacheTargetContextGeneration? ExpectedTargetContextGeneration { get; }

    public string? UnexpectedProviderFailureMessage { get; }
}

public sealed record DocumentCacheInternalOnlyCacheAheadRecoveryPreflightFacts
{
    public DocumentCacheInternalOnlyCacheAheadRecoveryPreflightFacts(
        DocumentCacheTargetContextGeneration? expectedTargetContextGeneration,
        DocumentCacheDownstreamPublicationHistoryObservation? downstreamPublicationHistory,
        string? unexpectedProviderFailureMessage = null
    )
    {
        ExpectedTargetContextGeneration = expectedTargetContextGeneration;
        DownstreamPublicationHistory = downstreamPublicationHistory;
        UnexpectedProviderFailureMessage = DocumentCachePreflightDiagnosticText.SanitizeNullable(
            unexpectedProviderFailureMessage
        );
    }

    public DocumentCacheTargetContextGeneration? ExpectedTargetContextGeneration { get; }

    public DocumentCacheDownstreamPublicationHistoryObservation? DownstreamPublicationHistory { get; }

    public string? UnexpectedProviderFailureMessage { get; }
}

public static class DocumentCachePreflightClassifier
{
    private const string NoMutationMessage =
        "Classifier performed no lifecycle, cache, work, latch, or provider-setting mutation.";

    public static DocumentCacheAdministrativeCommandResult ClassifyGuardedNewEmptyActivation(
        DocumentCacheGuardedNewEmptyActivationRequest request,
        DocumentCacheTargetObservation? targetObservation,
        DocumentCacheGuardedNewEmptyActivationPreflightFacts facts
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(facts);

        DocumentCacheAdministrativeCommandResult? commonRejection = ClassifyCommonTargetState(
            DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
            request.TargetKey,
            targetObservation,
            facts.ExpectedTargetContextGeneration,
            facts.UnexpectedProviderFailureMessage
        );
        if (commonRejection is not null)
        {
            return commonRejection;
        }

        DocumentCacheAdministrativeCommandResult? lifecycleRejection = ClassifyRequiredLifecycle(
            DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
            request.TargetKey,
            targetObservation!,
            requiredLifecycle: DocumentCacheLifecycleState.Disabled,
            rejectCacheAheadLatch: true
        );
        if (lifecycleRejection is not null)
        {
            return lifecycleRejection;
        }

        DocumentCacheAdministrativeCommandResult? expectedSourceRejection = ClassifyExpectedSource(
            DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
            request.TargetKey,
            targetObservation!,
            request.ExpectedPhysicalSourceFingerprint
        );
        if (expectedSourceRejection is not null)
        {
            return expectedSourceRejection;
        }

        DocumentCacheAdministrativeCommandResult? prerequisiteRejection = ClassifyActivationPrerequisites(
            DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
            request.TargetKey,
            targetObservation!,
            facts.ActivationProviderPrerequisites
        );
        if (prerequisiteRejection is not null)
        {
            return prerequisiteRejection;
        }

        if (facts.GuardedNewEmptyState is null)
        {
            return Rejected(
                DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
                request.TargetKey,
                targetObservation!,
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure,
                "Guarded new-empty activation state observation is required."
            );
        }

        if (!facts.GuardedNewEmptyState.IsEmpty)
        {
            return Rejected(
                DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
                request.TargetKey,
                targetObservation!,
                DocumentCacheAdministrativeCommandClassification.NonemptyGuardedActivationState,
                DocumentCacheTargetDiagnosticCategory.NonemptyGuardedActivationState,
                facts.GuardedNewEmptyState.Message
            );
        }

        return Eligible(
            DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
            request.TargetKey,
            targetObservation!
        );
    }

    public static DocumentCacheAdministrativeCommandResult ClassifyOfflineActivation(
        DocumentCacheOfflineActivationRequest request,
        DocumentCacheTargetObservation? targetObservation,
        DocumentCacheOfflineActivationPreflightFacts facts
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(facts);

        DocumentCacheAdministrativeCommandResult? offlineWriterAdmissionRejection =
            ClassifyOfflineWriterAdmission(
                DocumentCacheAdministrativeCommand.OfflineActivation,
                request.TargetKey,
                request.OfflineWriterAdmission,
                targetObservation
            );
        if (offlineWriterAdmissionRejection is not null)
        {
            return offlineWriterAdmissionRejection;
        }

        DocumentCacheAdministrativeCommandResult? commonRejection = ClassifyCommonTargetState(
            DocumentCacheAdministrativeCommand.OfflineActivation,
            request.TargetKey,
            targetObservation,
            facts.ExpectedTargetContextGeneration,
            facts.UnexpectedProviderFailureMessage
        );
        if (commonRejection is not null)
        {
            return commonRejection;
        }

        DocumentCacheAdministrativeCommandResult? lifecycleRejection = ClassifyOfflineActivationLifecycle(
            request.TargetKey,
            targetObservation!
        );
        if (lifecycleRejection is not null)
        {
            return lifecycleRejection;
        }

        DocumentCacheAdministrativeCommandResult? expectedSourceRejection = ClassifyExpectedSource(
            DocumentCacheAdministrativeCommand.OfflineActivation,
            request.TargetKey,
            targetObservation!,
            request.ExpectedPhysicalSourceFingerprint
        );
        if (expectedSourceRejection is not null)
        {
            return expectedSourceRejection;
        }

        DocumentCacheAdministrativeCommandResult? prerequisiteRejection = ClassifyActivationPrerequisites(
            DocumentCacheAdministrativeCommand.OfflineActivation,
            request.TargetKey,
            targetObservation!,
            facts.ActivationProviderPrerequisites
        );
        if (prerequisiteRejection is not null)
        {
            return prerequisiteRejection;
        }

        DocumentCacheDownstreamPublicationHistoryProofResult? downstreamProof =
            ClassifyDownstreamPublicationHistory(
                request.TargetKey,
                targetObservation!,
                facts.DownstreamPublicationHistory,
                request.ExpectedPhysicalSourceFingerprint
            );
        if (downstreamProof is { IsAccepted: false })
        {
            return RejectedFromDownstreamProof(
                DocumentCacheAdministrativeCommand.OfflineActivation,
                request.TargetKey,
                targetObservation!,
                downstreamProof
            );
        }

        return Eligible(
            DocumentCacheAdministrativeCommand.OfflineActivation,
            request.TargetKey,
            targetObservation!,
            downstreamProof!.DownstreamPublicationStatus
        );
    }

    public static DocumentCacheAdministrativeCommandResult ClassifyOfflineDeactivation(
        DocumentCacheOfflineDeactivationRequest request,
        DocumentCacheTargetObservation? targetObservation,
        DocumentCacheOfflineDeactivationPreflightFacts facts
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(facts);

        DocumentCacheAdministrativeCommandResult? offlineWriterAdmissionRejection =
            ClassifyOfflineWriterAdmission(
                DocumentCacheAdministrativeCommand.OfflineDeactivation,
                request.TargetKey,
                request.OfflineWriterAdmission,
                targetObservation
            );
        if (offlineWriterAdmissionRejection is not null)
        {
            return offlineWriterAdmissionRejection;
        }

        DocumentCacheAdministrativeCommandResult? commonRejection = ClassifyCommonTargetState(
            DocumentCacheAdministrativeCommand.OfflineDeactivation,
            request.TargetKey,
            targetObservation,
            facts.ExpectedTargetContextGeneration,
            facts.UnexpectedProviderFailureMessage
        );
        if (commonRejection is not null)
        {
            return commonRejection;
        }

        DocumentCacheAdministrativeCommandResult? lifecycleRejection = ClassifyOfflineDeactivationLifecycle(
            request.TargetKey,
            targetObservation!
        );
        if (lifecycleRejection is not null)
        {
            return lifecycleRejection;
        }

        DocumentCacheAdministrativeCommandResult? expectedSourceRejection = ClassifyExpectedSource(
            DocumentCacheAdministrativeCommand.OfflineDeactivation,
            request.TargetKey,
            targetObservation!,
            request.ExpectedPhysicalSourceFingerprint
        );
        if (expectedSourceRejection is not null)
        {
            return expectedSourceRejection;
        }

        DocumentCacheDownstreamPublicationHistoryProofResult? downstreamProof =
            ClassifyDownstreamPublicationHistory(
                request.TargetKey,
                targetObservation!,
                facts.DownstreamPublicationHistory,
                request.ExpectedPhysicalSourceFingerprint
            );
        if (downstreamProof is { IsAccepted: false })
        {
            return RejectedFromDownstreamProof(
                DocumentCacheAdministrativeCommand.OfflineDeactivation,
                request.TargetKey,
                targetObservation!,
                downstreamProof
            );
        }

        return Eligible(
            DocumentCacheAdministrativeCommand.OfflineDeactivation,
            request.TargetKey,
            targetObservation!,
            downstreamProof!.DownstreamPublicationStatus
        );
    }

    public static DocumentCacheAdministrativeCommandResult ClassifyOnlineCacheRebuild(
        DocumentCacheOnlineCacheRebuildRequest request,
        DocumentCacheTargetObservation? targetObservation,
        DocumentCacheOnlineCacheRebuildPreflightFacts facts
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(facts);

        DocumentCacheAdministrativeCommandResult? commonRejection = ClassifyCommonTargetState(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            request.TargetKey,
            targetObservation,
            facts.ExpectedTargetContextGeneration,
            facts.UnexpectedProviderFailureMessage
        );
        if (commonRejection is not null)
        {
            return commonRejection;
        }

        DocumentCacheAdministrativeCommandResult? lifecycleRejection = ClassifyOnlineRebuildLifecycle(
            request.TargetKey,
            targetObservation!
        );
        if (lifecycleRejection is not null)
        {
            return lifecycleRejection;
        }

        DocumentCacheAdministrativeCommandResult? expectedSourceRejection = ClassifyExpectedSource(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            request.TargetKey,
            targetObservation!,
            request.ExpectedPhysicalSourceFingerprint
        );
        if (expectedSourceRejection is not null)
        {
            return expectedSourceRejection;
        }

        return Eligible(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            request.TargetKey,
            targetObservation!
        );
    }

    public static DocumentCacheAdministrativeCommandResult ClassifyInternalOnlyCacheAheadRecovery(
        DocumentCacheInternalOnlyCacheAheadRecoveryRequest request,
        DocumentCacheTargetObservation? targetObservation,
        DocumentCacheInternalOnlyCacheAheadRecoveryPreflightFacts facts
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(facts);

        DocumentCacheAdministrativeCommandResult? offlineWriterAdmissionRejection =
            ClassifyOfflineWriterAdmission(
                DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
                request.TargetKey,
                request.OfflineWriterAdmission,
                targetObservation
            );
        if (offlineWriterAdmissionRejection is not null)
        {
            return offlineWriterAdmissionRejection;
        }

        DocumentCacheAdministrativeCommandResult? commonRejection = ClassifyCommonTargetState(
            DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
            request.TargetKey,
            targetObservation,
            facts.ExpectedTargetContextGeneration,
            facts.UnexpectedProviderFailureMessage
        );
        if (commonRejection is not null)
        {
            return commonRejection;
        }

        DocumentCacheAdministrativeCommandResult? lifecycleRejection =
            ClassifyInternalOnlyCacheAheadRecoveryLifecycle(request.TargetKey, targetObservation!);
        if (lifecycleRejection is not null)
        {
            return lifecycleRejection;
        }

        DocumentCacheAdministrativeCommandResult? expectedSourceRejection = ClassifyExpectedSource(
            DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
            request.TargetKey,
            targetObservation!,
            request.ExpectedPhysicalSourceFingerprint
        );
        if (expectedSourceRejection is not null)
        {
            return expectedSourceRejection;
        }

        DocumentCacheDownstreamPublicationHistoryProofResult? downstreamProof =
            ClassifyDownstreamPublicationHistory(
                request.TargetKey,
                targetObservation!,
                facts.DownstreamPublicationHistory,
                request.ExpectedPhysicalSourceFingerprint
            );
        if (downstreamProof is { IsAccepted: false })
        {
            return RejectedFromDownstreamProof(
                DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
                request.TargetKey,
                targetObservation!,
                downstreamProof
            );
        }

        return Eligible(
            DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
            request.TargetKey,
            targetObservation!,
            downstreamProof!.DownstreamPublicationStatus
        );
    }

    public static DocumentCacheAdministrativeCommandResult ClassifyExplicitIntegrityScrub(
        DocumentCacheExplicitIntegrityScrubRequest request,
        DocumentCacheTargetObservation? targetObservation,
        DocumentCacheExplicitIntegrityScrubPreflightFacts facts
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(facts);

        DocumentCacheAdministrativeCommandResult? commonRejection = ClassifyCommonTargetState(
            DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub,
            request.TargetKey,
            targetObservation,
            facts.ExpectedTargetContextGeneration,
            facts.UnexpectedProviderFailureMessage
        );
        if (commonRejection is not null)
        {
            return commonRejection;
        }

        DocumentCacheAdministrativeCommandResult? lifecycleRejection = ClassifyRequiredLifecycle(
            DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub,
            request.TargetKey,
            targetObservation!,
            requiredLifecycle: DocumentCacheLifecycleState.Tracking,
            rejectCacheAheadLatch: true
        );
        if (lifecycleRejection is not null)
        {
            return lifecycleRejection;
        }

        DocumentCacheAdministrativeCommandResult? expectedSourceRejection = ClassifyExpectedSource(
            DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub,
            request.TargetKey,
            targetObservation!,
            request.ExpectedPhysicalSourceFingerprint
        );
        if (expectedSourceRejection is not null)
        {
            return expectedSourceRejection;
        }

        return Eligible(
            DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub,
            request.TargetKey,
            targetObservation!
        );
    }

    public static DocumentCacheAdministrativeCommandResult? ClassifyOfflineWriterAdmission(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission,
        DocumentCacheTargetObservation? targetObservation = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        if (!RequiresOfflineWriterAdmission(command))
        {
            return null;
        }

        if (offlineWriterAdmission is null)
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.MissingOfflineWriterAdmission,
                DocumentCacheAdministrativeDiagnosticCategory.MissingOfflineWriterAdmission,
                "Offline writer admission is required for this command."
            );
        }

        if (!offlineWriterAdmission.Confirmed)
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.UnconfirmedOfflineWriterAdmission,
                DocumentCacheAdministrativeDiagnosticCategory.UnconfirmedOfflineWriterAdmission,
                "Offline writer admission must have confirmed true."
            );
        }

        if (
            offlineWriterAdmission.Confirmation is null
            && !offlineWriterAdmission.HasUnrecognizedConfirmation
        )
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.MissingOfflineWriterAdmission,
                DocumentCacheAdministrativeDiagnosticCategory.MissingOfflineWriterAdmission,
                "Offline writer admission confirmation is required for this command."
            );
        }

        DocumentCacheOfflineWriterAdmissionConfirmation expectedConfirmation =
            ExpectedOfflineWriterAdmissionConfirmation(command);
        if (offlineWriterAdmission.Confirmation != expectedConfirmation)
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.MismatchedOfflineWriterAdmission,
                DocumentCacheAdministrativeDiagnosticCategory.MismatchedOfflineWriterAdmission,
                "Offline writer admission confirmation must match the command-specific confirmation token."
            );
        }

        return null;
    }

    public static DocumentCacheAdministrativeCommandResult? ClassifyCommandConfirmation(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheAdministrativeCommandConfirmation? confirmation,
        DocumentCacheTargetObservation? targetObservation = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        if (confirmation is null)
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.MissingCommandConfirmation,
                DocumentCacheAdministrativeDiagnosticCategory.MissingCommandConfirmation,
                "Command confirmation is required for this administrative command."
            );
        }

        DocumentCacheAdministrativeCommandConfirmation expectedConfirmation = ExpectedCommandConfirmation(
            command
        );
        if (!Enum.IsDefined(confirmation.Value) || confirmation.Value != expectedConfirmation)
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.MismatchedCommandConfirmation,
                DocumentCacheAdministrativeDiagnosticCategory.MismatchedCommandConfirmation,
                "Command confirmation must match the command-specific confirmation token."
            );
        }

        return null;
    }

    public static DocumentCacheAdministrativeCommandResult? ClassifyTargetObservationFailure(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation? targetObservation
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        return ClassifyCommonTargetState(
            command,
            targetKey,
            targetObservation,
            expectedTargetContextGeneration: null,
            unexpectedProviderFailureMessage: null
        );
    }

    public static DocumentCacheOfflineWriterAdmissionConfirmation? AcceptedOfflineWriterAdmissionConfirmation(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission
    )
    {
        if (!RequiresOfflineWriterAdmission(command) || offlineWriterAdmission is null)
        {
            return null;
        }

        DocumentCacheOfflineWriterAdmissionConfirmation expectedConfirmation =
            ExpectedOfflineWriterAdmissionConfirmation(command);
        return
            offlineWriterAdmission is { Confirmed: true, Confirmation: var confirmation }
            && confirmation == expectedConfirmation
            ? confirmation
            : null;
    }

    public static DocumentCacheAdministrativeCommandConfirmation ExpectedCommandConfirmation(
        DocumentCacheAdministrativeCommand command
    ) => DocumentCacheAdministrativeCommandContracts.ExpectedCommandConfirmation(command);

    private static DocumentCacheAdministrativeCommandResult? ClassifyCommonTargetState(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation? targetObservation,
        DocumentCacheTargetContextGeneration? expectedTargetContextGeneration,
        string? unexpectedProviderFailureMessage
    )
    {
        if (targetObservation is null)
        {
            return Rejected(
                command,
                targetKey,
                targetObservation: null,
                DocumentCacheAdministrativeCommandClassification.TargetNotConfigured,
                DocumentCacheTargetDiagnosticCategory.TargetNotConfigured,
                "DocumentCache target is not configured in the current process."
            );
        }

        if (!targetKey.TargetKey.Equals(targetObservation.TargetKey))
        {
            throw new ArgumentException(
                "Target observation must be bound to the request target key.",
                nameof(targetObservation)
            );
        }

        if (
            targetObservation.ResolutionState
            is DocumentCacheTargetResolutionState.Configured
                or DocumentCacheTargetResolutionState.Unresolved
        )
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.TargetUnresolved,
                DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
                "DocumentCache target is not resolved.",
                ConvertDiagnostics(targetObservation.Diagnostics)
            );
        }

        if (
            expectedTargetContextGeneration is not null
            && targetObservation.Generation?.Value != expectedTargetContextGeneration.Value
        )
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.TargetReplacedBeforeExecution,
                DocumentCacheTargetDiagnosticCategory.TargetReplaced,
                "DocumentCache target context generation was replaced before execution.",
                ConvertDiagnostics(targetObservation.Diagnostics)
            );
        }

        DocumentCacheAdministrativeCommandResult? targetContextRejection = ClassifyTargetContextFailure(
            command,
            targetKey,
            targetObservation
        );
        if (targetContextRejection is not null)
        {
            return targetContextRejection;
        }

        if (unexpectedProviderFailureMessage is not null)
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure,
                unexpectedProviderFailureMessage
            );
        }

        return null;
    }

    private static bool RequiresOfflineWriterAdmission(DocumentCacheAdministrativeCommand command) =>
        DocumentCacheAdministrativeCommandContracts.RequiresOfflineWriterAdmission(command);

    private static DocumentCacheOfflineWriterAdmissionConfirmation ExpectedOfflineWriterAdmissionConfirmation(
        DocumentCacheAdministrativeCommand command
    ) => DocumentCacheAdministrativeCommandContracts.ExpectedOfflineWriterAdmissionConfirmation(command);

    private static DocumentCacheAdministrativeCommandResult? ClassifyTargetContextFailure(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation targetObservation
    )
    {
        if (targetObservation.EligibilityState == DocumentCacheTargetEligibilityState.Eligible)
        {
            return null;
        }

        ImmutableArray<DocumentCacheAdministrativeDiagnostic> diagnostics = ConvertDiagnostics(
            targetObservation.Diagnostics
        );
        ImmutableHashSet<DocumentCacheTargetDiagnosticCategory> categories = targetObservation
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .ToImmutableHashSet();

        if (categories.Contains(DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident))
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.UnsupportedPrerequisiteIncident,
                DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident,
                "Provider prerequisite failure was observed outside the supported lifecycle.",
                diagnostics
            );
        }

        if (categories.Contains(DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed))
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.ProviderPrerequisiteFailed,
                DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed,
                "Provider prerequisite failed for the target context.",
                diagnostics
            );
        }

        if (categories.Contains(DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing))
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.ProviderMetadataMissing,
                DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing,
                "DocumentCache target is missing relational provider metadata.",
                diagnostics
            );
        }

        if (categories.Contains(DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown))
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.ProviderMetadataUnknown,
                DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown,
                "DocumentCache target has unknown relational provider metadata.",
                diagnostics
            );
        }

        if (categories.Contains(DocumentCacheTargetDiagnosticCategory.ProviderMismatch))
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.ProviderMismatch,
                DocumentCacheTargetDiagnosticCategory.ProviderMismatch,
                "DocumentCache target provider does not match this DMS process provider.",
                diagnostics
            );
        }

        if (categories.Contains(DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing))
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.ConnectionInputMissing,
                DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing,
                "DocumentCache target has no usable connection input.",
                diagnostics
            );
        }

        if (
            categories.Overlaps([
                DocumentCacheTargetDiagnosticCategory.PhysicalSourceFingerprintFailure,
                DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure,
                DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                DocumentCacheTargetDiagnosticCategory.EnqueueTriggerFailure,
            ])
            || targetObservation.Inventory?.Status is not null and not DocumentCacheInventoryStatus.Satisfied
            || targetObservation.EnqueueTrigger?.Status
                is not null
                    and not DocumentCacheEnqueueTriggerStatus.Satisfied
        )
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.MissingOrInvalidInventory,
                DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                "DocumentCache target inventory is missing or invalid.",
                diagnostics
            );
        }

        return Rejected(
            command,
            targetKey,
            targetObservation,
            DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
            DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure,
            "DocumentCache target context is not eligible for command preflight.",
            diagnostics
        );
    }

    private static DocumentCacheAdministrativeCommandResult? ClassifyActivationPrerequisites(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheProviderPrerequisiteValidationResult? activationProviderPrerequisites
    )
    {
        if (activationProviderPrerequisites is null)
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure,
                "Command-time activation provider prerequisite observation is required."
            );
        }

        if (activationProviderPrerequisites.IsSatisfied)
        {
            return null;
        }

        DocumentCacheTargetDiagnosticCategory diagnosticCategory =
            activationProviderPrerequisites.FailureCategory
            ?? DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed;
        DocumentCacheAdministrativeCommandClassification classification =
            diagnosticCategory == DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident
                ? DocumentCacheAdministrativeCommandClassification.UnsupportedPrerequisiteIncident
                : DocumentCacheAdministrativeCommandClassification.ProviderPrerequisiteFailed;

        return Rejected(
            command,
            targetKey,
            targetObservation,
            classification,
            diagnosticCategory,
            activationProviderPrerequisites.Message
        );
    }

    private static DocumentCacheAdministrativeCommandResult? ClassifyRequiredLifecycle(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheLifecycleState requiredLifecycle,
        bool rejectCacheAheadLatch
    )
    {
        DocumentCacheLifecycleObservation lifecycle = targetObservation.Lifecycle!;
        if (lifecycle.State == DocumentCacheLifecycleState.Resetting)
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.ResettingRequiresExplicitOperatorRecovery,
                DocumentCacheTargetDiagnosticCategory.ResettingRequiresExplicitOperatorRecovery,
                "DocumentCache target is already Resetting and requires explicit operator recovery."
            );
        }

        if (lifecycle.State != requiredLifecycle)
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
                DocumentCacheTargetDiagnosticCategory.LifecycleMismatch,
                "DocumentCache lifecycle does not match the command preflight requirement."
            );
        }

        if (rejectCacheAheadLatch && lifecycle.CacheAheadRecoveryRequired)
        {
            return Rejected(
                command,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
                DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet,
                "DocumentCache cache-ahead recovery latch is set."
            );
        }

        return null;
    }

    private static DocumentCacheAdministrativeCommandResult? ClassifyOfflineDeactivationLifecycle(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation targetObservation
    )
    {
        DocumentCacheLifecycleObservation lifecycle = targetObservation.Lifecycle!;
        if (lifecycle.CacheAheadRecoveryRequired)
        {
            return Rejected(
                DocumentCacheAdministrativeCommand.OfflineDeactivation,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
                DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet,
                "DocumentCache cache-ahead recovery latch is set."
            );
        }

        if (
            lifecycle.State
            is DocumentCacheLifecycleState.Tracking
                or DocumentCacheLifecycleState.Resetting
                or DocumentCacheLifecycleState.Rebuilding
        )
        {
            return null;
        }

        return Rejected(
            DocumentCacheAdministrativeCommand.OfflineDeactivation,
            targetKey,
            targetObservation,
            DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
            DocumentCacheTargetDiagnosticCategory.LifecycleMismatch,
            "DocumentCache lifecycle does not match the command preflight requirement."
        );
    }

    private static DocumentCacheAdministrativeCommandResult? ClassifyOfflineActivationLifecycle(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation targetObservation
    )
    {
        DocumentCacheLifecycleObservation lifecycle = targetObservation.Lifecycle!;
        if (lifecycle.CacheAheadRecoveryRequired)
        {
            return Rejected(
                DocumentCacheAdministrativeCommand.OfflineActivation,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
                DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet,
                "DocumentCache cache-ahead recovery latch is set."
            );
        }

        if (lifecycle.State == DocumentCacheLifecycleState.Resetting)
        {
            return Rejected(
                DocumentCacheAdministrativeCommand.OfflineActivation,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.ResettingRequiresExplicitOperatorRecovery,
                DocumentCacheTargetDiagnosticCategory.ResettingRequiresExplicitOperatorRecovery,
                "DocumentCache target is already Resetting and requires explicit operator recovery."
            );
        }

        if (lifecycle.State is DocumentCacheLifecycleState.Disabled or DocumentCacheLifecycleState.Rebuilding)
        {
            return null;
        }

        return Rejected(
            DocumentCacheAdministrativeCommand.OfflineActivation,
            targetKey,
            targetObservation,
            DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
            DocumentCacheTargetDiagnosticCategory.LifecycleMismatch,
            "DocumentCache lifecycle does not match the command preflight requirement."
        );
    }

    private static DocumentCacheAdministrativeCommandResult? ClassifyOnlineRebuildLifecycle(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation targetObservation
    )
    {
        DocumentCacheLifecycleObservation lifecycle = targetObservation.Lifecycle!;
        if (lifecycle.CacheAheadRecoveryRequired)
        {
            return Rejected(
                DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                targetKey,
                targetObservation,
                DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
                DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet,
                "DocumentCache cache-ahead recovery latch is set."
            );
        }

        if (
            lifecycle.State
            is DocumentCacheLifecycleState.Tracking
                or DocumentCacheLifecycleState.Resetting
                or DocumentCacheLifecycleState.Rebuilding
        )
        {
            return null;
        }

        return Rejected(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            targetKey,
            targetObservation,
            DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
            DocumentCacheTargetDiagnosticCategory.LifecycleMismatch,
            "DocumentCache lifecycle does not match the command preflight requirement."
        );
    }

    private static DocumentCacheAdministrativeCommandResult? ClassifyInternalOnlyCacheAheadRecoveryLifecycle(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation targetObservation
    )
    {
        DocumentCacheLifecycleObservation lifecycle = targetObservation.Lifecycle!;
        if (
            lifecycle.CacheAheadRecoveryRequired
            && lifecycle.State
                is DocumentCacheLifecycleState.Tracking
                    or DocumentCacheLifecycleState.Resetting
                    or DocumentCacheLifecycleState.Rebuilding
        )
        {
            return null;
        }

        if (lifecycle is { State: DocumentCacheLifecycleState.Rebuilding, CacheAheadRecoveryRequired: false })
        {
            return null;
        }

        return Rejected(
            DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
            targetKey,
            targetObservation,
            DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
            DocumentCacheTargetDiagnosticCategory.LifecycleMismatch,
            "Internal-only cache-ahead recovery requires Tracking, Resetting, or Rebuilding with a set cache-ahead latch, or Rebuilding with a clear latch as the supported resume state."
        );
    }

    private static DocumentCacheAdministrativeCommandResult? ClassifyExpectedSource(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation targetObservation,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint
    )
    {
        if (
            expectedPhysicalSourceFingerprint is null
            || targetObservation.PhysicalSourceFingerprint?.Equals(expectedPhysicalSourceFingerprint) == true
        )
        {
            return null;
        }

        return Rejected(
            command,
            targetKey,
            targetObservation,
            DocumentCacheAdministrativeCommandClassification.ExpectedSourceMismatch,
            DocumentCacheTargetDiagnosticCategory.ExpectedSourceMismatch,
            "Expected physical-source fingerprint does not match the current target observation."
        );
    }

    private static DocumentCacheDownstreamPublicationHistoryProofResult ClassifyDownstreamPublicationHistory(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheDownstreamPublicationHistoryObservation? downstreamPublicationHistory,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint
    )
    {
        if (downstreamPublicationHistory is null)
        {
            DocumentCacheDownstreamPublicationHistoryObservation missingObservation = new(
                targetKey.TargetKey,
                targetObservation.PhysicalSourceFingerprint,
                DocumentCacheDownstreamPublicationStatus.Unknown,
                evidenceSource: "document-cache-command-preflight",
                evidenceGenerationIdentifier: null,
                DateTimeOffset.UnixEpoch,
                "Downstream publication history observation is required."
            );

            return DocumentCacheDownstreamPublicationHistoryProofResult.Rejected(
                DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown,
                missingObservation,
                new DocumentCacheAdministrativeDiagnostic(
                    DocumentCacheTargetDiagnosticCategory.DownstreamPublicationHistoryPresentOrUnknown,
                    "Downstream publication history observation is required."
                )
            );
        }

        return DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
            targetKey.TargetKey,
            targetObservation.PhysicalSourceFingerprint,
            downstreamPublicationHistory,
            expectedPhysicalSourceFingerprint
        );
    }

    private static DocumentCacheAdministrativeCommandResult RejectedFromDownstreamProof(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheDownstreamPublicationHistoryProofResult proof
    ) =>
        Rejected(
            command,
            targetKey,
            targetObservation,
            proof.Classification,
            proof.Diagnostics[0].Category,
            proof.Diagnostics[0].Message,
            proof.Diagnostics,
            proof.DownstreamPublicationStatus
        );

    private static DocumentCacheAdministrativeCommandResult Eligible(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheDownstreamPublicationStatus? downstreamPublicationStatus = null
    ) =>
        new(
            command,
            targetKey,
            DocumentCacheAdministrativeCommandClassification.Succeeded,
            targetObservation.Lifecycle?.State,
            targetObservation.Lifecycle?.CacheAheadRecoveryRequired,
            targetObservation.PhysicalSourceFingerprint,
            targetObservation.Generation?.Value,
            downstreamPublicationStatus
        );

    private static DocumentCacheAdministrativeCommandResult Rejected(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation? targetObservation,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheTargetDiagnosticCategory category,
        string message,
        ImmutableArray<DocumentCacheAdministrativeDiagnostic> diagnostics = default,
        DocumentCacheDownstreamPublicationStatus? downstreamPublicationStatus = null
    ) =>
        Rejected(
            command,
            targetKey,
            targetObservation,
            classification,
            new DocumentCacheAdministrativeDiagnostic(category, message).Category,
            message,
            diagnostics,
            downstreamPublicationStatus
        );

    private static DocumentCacheAdministrativeCommandResult Rejected(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheTargetObservation? targetObservation,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory category,
        string message,
        ImmutableArray<DocumentCacheAdministrativeDiagnostic> diagnostics = default,
        DocumentCacheDownstreamPublicationStatus? downstreamPublicationStatus = null
    )
    {
        ImmutableArray<DocumentCacheAdministrativeDiagnostic> resultDiagnostics = diagnostics.IsDefaultOrEmpty
            ? [new DocumentCacheAdministrativeDiagnostic(category, message)]
            : diagnostics;

        return new(
            command,
            targetKey,
            classification,
            targetObservation?.Lifecycle?.State,
            targetObservation?.Lifecycle?.CacheAheadRecoveryRequired,
            targetObservation?.PhysicalSourceFingerprint,
            targetObservation?.Generation?.Value,
            downstreamPublicationStatus,
            resultDiagnostics,
            new DocumentCacheAdministrativeNoMutationGuarantee(
                guaranteed: true,
                DocumentCacheAdministrativeNoMutationScope.LifecycleCacheWorkLatchAndProviderSettings,
                NoMutationMessage
            )
        );
    }

    private static ImmutableArray<DocumentCacheAdministrativeDiagnostic> ConvertDiagnostics(
        ImmutableArray<DocumentCacheTargetDiagnostic> diagnostics
    ) =>
        diagnostics
            .Select(diagnostic => new DocumentCacheAdministrativeDiagnostic(
                diagnostic.Category,
                diagnostic.Message
            ))
            .ToImmutableArray();
}

file static class DocumentCachePreflightDiagnosticText
{
    public static string? SanitizeNullable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string sanitized = DocumentCacheDiagnosticText.Sanitize(value);
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }
}
