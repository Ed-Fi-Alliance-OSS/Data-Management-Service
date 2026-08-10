// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

internal enum DocumentCacheWriterCandidateMetadataComparison
{
    NotSupplied = 1,
    MatchesCurrentSource = 2,
    DocumentUuidMismatch = 3,
    ResourceMetadataMismatch = 4,
    TargetMappingMismatch = 5,
}

internal sealed record DocumentCacheWriterCandidateObservation
{
    public DocumentCacheWriterCandidateObservation(
        DocumentCacheMaterializationCandidate? candidate,
        DocumentCacheWriterCandidateMetadataComparison metadataComparison
    )
    {
        MetadataComparison = DocumentCacheMaterializerGuards.RequireDefined(
            metadataComparison,
            nameof(metadataComparison),
            "Unsupported cache-writer candidate metadata comparison."
        );

        if (
            candidate is null
            && metadataComparison != DocumentCacheWriterCandidateMetadataComparison.NotSupplied
        )
        {
            throw new ArgumentException(
                "Missing candidates must use the NotSupplied metadata comparison.",
                nameof(metadataComparison)
            );
        }

        if (
            candidate is not null
            && metadataComparison == DocumentCacheWriterCandidateMetadataComparison.NotSupplied
        )
        {
            throw new ArgumentException(
                "Supplied candidates require a metadata comparison against current durable source metadata.",
                nameof(metadataComparison)
            );
        }

        Candidate = candidate;
    }

    public static DocumentCacheWriterCandidateObservation Absent { get; } =
        new(null, DocumentCacheWriterCandidateMetadataComparison.NotSupplied);

    public DocumentCacheMaterializationCandidate? Candidate { get; }

    public DocumentCacheWriterCandidateMetadataComparison MetadataComparison { get; }

    public bool HasCandidate => Candidate is not null;
}

internal sealed record DocumentCacheWriterCurrentStateObservation
{
    public DocumentCacheWriterCurrentStateObservation(
        long? sourceContentVersion,
        long? cacheContentVersion,
        long? workRequiredContentVersion
    )
    {
        SourceContentVersion = RequirePositiveWhenSupplied(
            sourceContentVersion,
            nameof(sourceContentVersion)
        );
        CacheContentVersion = RequirePositiveWhenSupplied(cacheContentVersion, nameof(cacheContentVersion));
        WorkRequiredContentVersion = RequirePositiveWhenSupplied(
            workRequiredContentVersion,
            nameof(workRequiredContentVersion)
        );
    }

    public long? SourceContentVersion { get; }

    public long? CacheContentVersion { get; }

    public long? WorkRequiredContentVersion { get; }

    private static long? RequirePositiveWhenSupplied(long? value, string parameterName)
    {
        if (value is <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} must be positive.");
        }

        return value;
    }
}

internal sealed record DocumentCacheWriterClassificationRequest
{
    public DocumentCacheWriterClassificationRequest(
        DocumentCacheLifecycleReadResult lifecycleReadResult,
        DocumentCacheWriterCurrentStateObservation currentState,
        DocumentCacheWriterCandidateObservation candidateObservation
    )
    {
        LifecycleReadResult =
            lifecycleReadResult ?? throw new ArgumentNullException(nameof(lifecycleReadResult));
        CurrentState = currentState ?? throw new ArgumentNullException(nameof(currentState));
        CandidateObservation =
            candidateObservation ?? throw new ArgumentNullException(nameof(candidateObservation));
    }

    public DocumentCacheLifecycleReadResult LifecycleReadResult { get; }

    public DocumentCacheWriterCurrentStateObservation CurrentState { get; }

    public DocumentCacheWriterCandidateObservation CandidateObservation { get; }
}

internal enum DocumentCacheWriterSelectedAction
{
    ReturnLifecycleOrLatchFence = 1,
    ReturnSourceMissingOrDeleted = 2,
    RequestCacheAheadLatchFlow = 3,
    AcknowledgeAlreadyCurrentWork = 4,
    ReturnAlreadyCurrentWithoutWork = 5,
    WriteCandidateThenAcknowledgeWork = 6,
    ReturnNeedsMaterialization = 7,
    ReturnStaleCandidateSuppressed = 8,
    ReturnWorkAnomaly = 9,
    ReturnDeterministicInvariantOrTargetFailure = 10,
}

internal sealed record DocumentCacheWriterClassificationSelection
{
    private DocumentCacheWriterClassificationSelection(
        DocumentCacheWriterSelectedAction action,
        DocumentCacheWriterOutcome outcome,
        long? expectedContentVersion,
        DocumentCacheMaterializationCandidate? candidate,
        DocumentCacheWriterResult? terminalResult
    )
    {
        Action = DocumentCacheMaterializerGuards.RequireDefined(
            action,
            nameof(action),
            "Unsupported cache-writer selected action."
        );
        Outcome = DocumentCacheMaterializerGuards.RequireDefined(
            outcome,
            nameof(outcome),
            "Unsupported cache-writer selected outcome."
        );
        ExpectedContentVersion = RequirePositiveWhenSupplied(
            expectedContentVersion,
            nameof(expectedContentVersion)
        );
        Candidate = candidate;
        TerminalResult = terminalResult;

        if (WritesCache && Candidate is null)
        {
            throw new ArgumentException("Cache-write selections require a candidate.", nameof(candidate));
        }

        if (WritesCache && Candidate!.ContentVersion != ExpectedContentVersion)
        {
            throw new ArgumentException(
                "Cache-write selections require the candidate ContentVersion to match the expected acknowledgement version.",
                nameof(candidate)
            );
        }

        if (AcknowledgesWork && ExpectedContentVersion is null)
        {
            throw new ArgumentException(
                "Work-acknowledgement selections require an expected content version.",
                nameof(expectedContentVersion)
            );
        }

        if (RequiresProviderCompletion && TerminalResult is not null)
        {
            throw new ArgumentException(
                "Provider-action selections must not carry a terminal result.",
                nameof(terminalResult)
            );
        }

        if (!RequiresProviderCompletion && TerminalResult is null)
        {
            throw new ArgumentException(
                "Terminal selections require a bounded writer result.",
                nameof(terminalResult)
            );
        }

        if (TerminalResult is not null && TerminalResult.Outcome != Outcome)
        {
            throw new ArgumentException(
                "Terminal writer result outcome must match the selected outcome.",
                nameof(terminalResult)
            );
        }
    }

    public DocumentCacheWriterSelectedAction Action { get; }

    public DocumentCacheWriterOutcome Outcome { get; }

    public long? ExpectedContentVersion { get; }

    public DocumentCacheMaterializationCandidate? Candidate { get; }

    public DocumentCacheWriterResult? TerminalResult { get; }

    public bool WritesCache => Action is DocumentCacheWriterSelectedAction.WriteCandidateThenAcknowledgeWork;

    public bool AcknowledgesWork =>
        Action
            is DocumentCacheWriterSelectedAction.AcknowledgeAlreadyCurrentWork
                or DocumentCacheWriterSelectedAction.WriteCandidateThenAcknowledgeWork;

    public bool RequestsCacheAheadLatchFlow =>
        Action == DocumentCacheWriterSelectedAction.RequestCacheAheadLatchFlow;

    public bool RequiresProviderCompletion =>
        Action
            is DocumentCacheWriterSelectedAction.AcknowledgeAlreadyCurrentWork
                or DocumentCacheWriterSelectedAction.WriteCandidateThenAcknowledgeWork
                or DocumentCacheWriterSelectedAction.RequestCacheAheadLatchFlow;

    public static DocumentCacheWriterClassificationSelection LifecycleOrLatchFence(
        DocumentCacheWriterFenceReason reason,
        DocumentCacheLifecycleState? lifecycleState,
        bool? cacheAheadRecoveryRequired
    ) =>
        new(
            DocumentCacheWriterSelectedAction.ReturnLifecycleOrLatchFence,
            DocumentCacheWriterOutcome.LifecycleOrLatchFenced,
            expectedContentVersion: null,
            candidate: null,
            terminalResult: new DocumentCacheWriterResult.LifecycleOrLatchFenced(
                reason,
                lifecycleState,
                cacheAheadRecoveryRequired
            )
        );

    public static DocumentCacheWriterClassificationSelection SourceMissingOrDeleted() =>
        new(
            DocumentCacheWriterSelectedAction.ReturnSourceMissingOrDeleted,
            DocumentCacheWriterOutcome.SourceMissingOrDeleted,
            expectedContentVersion: null,
            candidate: null,
            terminalResult: DocumentCacheWriterResult.SourceMissingOrDeleted.Instance
        );

    public static DocumentCacheWriterClassificationSelection RequestCacheAheadLatchFlow(
        long sourceContentVersion,
        long cacheContentVersion
    )
    {
        DocumentCacheMaterializerGuards.RequireCacheAheadLatchVersions(
            sourceContentVersion,
            cacheContentVersion
        );

        return new(
            DocumentCacheWriterSelectedAction.RequestCacheAheadLatchFlow,
            DocumentCacheWriterOutcome.CacheAheadLatchSet,
            expectedContentVersion: null,
            candidate: null,
            terminalResult: null
        );
    }

    public static DocumentCacheWriterClassificationSelection AcknowledgeAlreadyCurrentWork(
        long expectedContentVersion
    ) =>
        new(
            DocumentCacheWriterSelectedAction.AcknowledgeAlreadyCurrentWork,
            DocumentCacheWriterOutcome.AlreadyCurrentAcknowledged,
            expectedContentVersion,
            candidate: null,
            terminalResult: null
        );

    public static DocumentCacheWriterClassificationSelection AlreadyCurrentWithoutWork(
        long currentContentVersion
    ) =>
        new(
            DocumentCacheWriterSelectedAction.ReturnAlreadyCurrentWithoutWork,
            DocumentCacheWriterOutcome.AlreadyCurrentNoWork,
            expectedContentVersion: null,
            candidate: null,
            terminalResult: new DocumentCacheWriterResult.AlreadyCurrentNoWork(currentContentVersion)
        );

    public static DocumentCacheWriterClassificationSelection WriteCandidateThenAcknowledgeWork(
        DocumentCacheMaterializationCandidate candidate,
        long expectedContentVersion
    ) =>
        new(
            DocumentCacheWriterSelectedAction.WriteCandidateThenAcknowledgeWork,
            DocumentCacheWriterOutcome.CandidateWrittenAcknowledged,
            expectedContentVersion,
            candidate ?? throw new ArgumentNullException(nameof(candidate)),
            terminalResult: null
        );

    public static DocumentCacheWriterClassificationSelection NeedsMaterialization(
        long currentContentVersion
    ) =>
        new(
            DocumentCacheWriterSelectedAction.ReturnNeedsMaterialization,
            DocumentCacheWriterOutcome.NeedsMaterialization,
            expectedContentVersion: null,
            candidate: null,
            terminalResult: new DocumentCacheWriterResult.NeedsMaterialization(currentContentVersion)
        );

    public static DocumentCacheWriterClassificationSelection StaleCandidateSuppressed(
        long currentContentVersion,
        long candidateContentVersion
    ) =>
        new(
            DocumentCacheWriterSelectedAction.ReturnStaleCandidateSuppressed,
            DocumentCacheWriterOutcome.StaleCandidateSuppressed,
            expectedContentVersion: null,
            candidate: null,
            terminalResult: new DocumentCacheWriterResult.StaleCandidateSuppressed(
                currentContentVersion,
                candidateContentVersion
            )
        );

    public static DocumentCacheWriterClassificationSelection WorkAnomaly(
        DocumentCacheWriterWorkAnomalyKind kind,
        DocumentCacheLifecycleState lifecycleState,
        long? currentSourceContentVersion,
        long? workRequiredContentVersion
    ) =>
        new(
            DocumentCacheWriterSelectedAction.ReturnWorkAnomaly,
            DocumentCacheWriterOutcome.WorkAnomaly,
            expectedContentVersion: null,
            candidate: null,
            terminalResult: new DocumentCacheWriterResult.WorkAnomaly(
                kind,
                lifecycleState,
                currentSourceContentVersion,
                workRequiredContentVersion
            )
        );

    public static DocumentCacheWriterClassificationSelection DeterministicInvariantOrTargetFailure(
        DocumentCacheWriterInvariantFailureReason reason,
        long currentContentVersion,
        long candidateContentVersion
    ) =>
        new(
            DocumentCacheWriterSelectedAction.ReturnDeterministicInvariantOrTargetFailure,
            DocumentCacheWriterOutcome.DeterministicInvariantOrTargetFailure,
            expectedContentVersion: null,
            candidate: null,
            terminalResult: new DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure(
                reason,
                currentContentVersion,
                candidateContentVersion
            )
        );

    private static long? RequirePositiveWhenSupplied(long? value, string parameterName)
    {
        if (value is <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} must be positive.");
        }

        return value;
    }
}

internal static class DocumentCacheWriterClassificationSelector
{
    public static DocumentCacheWriterClassificationSelection Select(
        DocumentCacheWriterClassificationRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        DocumentCacheWriterClassificationSelection? lifecycleFence = SelectLifecycleFence(
            request.LifecycleReadResult
        );
        if (lifecycleFence is not null)
        {
            return lifecycleFence;
        }

        DocumentCacheLifecycleState lifecycleState = request.LifecycleReadResult.Lifecycle!.State;
        DocumentCacheWriterCurrentStateObservation currentState = request.CurrentState;

        if (currentState.SourceContentVersion is null)
        {
            return DocumentCacheWriterClassificationSelection.SourceMissingOrDeleted();
        }

        long sourceContentVersion = currentState.SourceContentVersion.Value;

        if (
            currentState.CacheContentVersion is long cacheContentVersion
            && cacheContentVersion > sourceContentVersion
        )
        {
            return DocumentCacheWriterClassificationSelection.RequestCacheAheadLatchFlow(
                sourceContentVersion,
                cacheContentVersion
            );
        }

        DocumentCacheWriterClassificationSelection? matchingVersionInvariant =
            SelectMatchingVersionCandidateInvariant(sourceContentVersion, request.CandidateObservation);
        if (matchingVersionInvariant is not null)
        {
            return matchingVersionInvariant;
        }

        if (
            currentState.CacheContentVersion == sourceContentVersion
            && currentState.WorkRequiredContentVersion == sourceContentVersion
        )
        {
            return DocumentCacheWriterClassificationSelection.AcknowledgeAlreadyCurrentWork(
                sourceContentVersion
            );
        }

        if (
            currentState.CacheContentVersion == sourceContentVersion
            && currentState.WorkRequiredContentVersion is null
        )
        {
            return DocumentCacheWriterClassificationSelection.AlreadyCurrentWithoutWork(sourceContentVersion);
        }

        if (
            currentState.WorkRequiredContentVersion == sourceContentVersion
            && CacheIsAbsentOrBehind(currentState.CacheContentVersion, sourceContentVersion)
        )
        {
            return SelectPendingProjectionAction(sourceContentVersion, request.CandidateObservation);
        }

        if (
            currentState.WorkRequiredContentVersion is null
            && CacheIsAbsentOrBehind(currentState.CacheContentVersion, sourceContentVersion)
        )
        {
            return DocumentCacheWriterClassificationSelection.WorkAnomaly(
                DocumentCacheWriterWorkAnomalyKind.MissingWork,
                lifecycleState,
                sourceContentVersion,
                workRequiredContentVersion: null
            );
        }

        return DocumentCacheWriterClassificationSelection.WorkAnomaly(
            DocumentCacheWriterWorkAnomalyKind.WorkVersionMismatch,
            lifecycleState,
            sourceContentVersion,
            currentState.WorkRequiredContentVersion
        );
    }

    private static DocumentCacheWriterClassificationSelection? SelectLifecycleFence(
        DocumentCacheLifecycleReadResult lifecycleReadResult
    )
    {
        if (!Enum.IsDefined(lifecycleReadResult.Status))
        {
            return DocumentCacheWriterClassificationSelection.LifecycleOrLatchFence(
                DocumentCacheWriterFenceReason.StateInvalid,
                lifecycleState: null,
                cacheAheadRecoveryRequired: null
            );
        }

        if (!lifecycleReadResult.Succeeded)
        {
            return DocumentCacheWriterClassificationSelection.LifecycleOrLatchFence(
                lifecycleReadResult.Status switch
                {
                    DocumentCacheLifecycleReadStatus.Missing => DocumentCacheWriterFenceReason.StateMissing,
                    DocumentCacheLifecycleReadStatus.Invalid => DocumentCacheWriterFenceReason.StateInvalid,
                    DocumentCacheLifecycleReadStatus.Unreadable =>
                        DocumentCacheWriterFenceReason.StateUnreadable,
                    _ => DocumentCacheWriterFenceReason.StateInvalid,
                },
                lifecycleState: null,
                cacheAheadRecoveryRequired: null
            );
        }

        DocumentCacheLifecycleObservation lifecycle = lifecycleReadResult.Lifecycle!;
        if (!Enum.IsDefined(lifecycle.State))
        {
            return DocumentCacheWriterClassificationSelection.LifecycleOrLatchFence(
                DocumentCacheWriterFenceReason.StateInvalid,
                lifecycleState: null,
                lifecycle.CacheAheadRecoveryRequired
            );
        }

        if (
            lifecycle.State
            is not (DocumentCacheLifecycleState.Tracking or DocumentCacheLifecycleState.Rebuilding)
        )
        {
            return DocumentCacheWriterClassificationSelection.LifecycleOrLatchFence(
                DocumentCacheWriterFenceReason.LifecycleNotEligible,
                lifecycle.State,
                lifecycle.CacheAheadRecoveryRequired
            );
        }

        if (lifecycle.CacheAheadRecoveryRequired)
        {
            return DocumentCacheWriterClassificationSelection.LifecycleOrLatchFence(
                DocumentCacheWriterFenceReason.CacheAheadRecoveryRequired,
                lifecycle.State,
                lifecycle.CacheAheadRecoveryRequired
            );
        }

        return null;
    }

    private static DocumentCacheWriterClassificationSelection? SelectMatchingVersionCandidateInvariant(
        long sourceContentVersion,
        DocumentCacheWriterCandidateObservation candidateObservation
    )
    {
        if (
            candidateObservation.Candidate is null
            || candidateObservation.Candidate.ContentVersion != sourceContentVersion
        )
        {
            return null;
        }

        return candidateObservation.MetadataComparison switch
        {
            DocumentCacheWriterCandidateMetadataComparison.MatchesCurrentSource => null,
            DocumentCacheWriterCandidateMetadataComparison.DocumentUuidMismatch =>
                DocumentCacheWriterClassificationSelection.DeterministicInvariantOrTargetFailure(
                    DocumentCacheWriterInvariantFailureReason.MatchingVersionDocumentUuidMismatch,
                    sourceContentVersion,
                    candidateObservation.Candidate.ContentVersion
                ),
            DocumentCacheWriterCandidateMetadataComparison.ResourceMetadataMismatch =>
                DocumentCacheWriterClassificationSelection.DeterministicInvariantOrTargetFailure(
                    DocumentCacheWriterInvariantFailureReason.MatchingVersionResourceMetadataMismatch,
                    sourceContentVersion,
                    candidateObservation.Candidate.ContentVersion
                ),
            DocumentCacheWriterCandidateMetadataComparison.TargetMappingMismatch =>
                DocumentCacheWriterClassificationSelection.DeterministicInvariantOrTargetFailure(
                    DocumentCacheWriterInvariantFailureReason.TargetMappingMismatch,
                    sourceContentVersion,
                    candidateObservation.Candidate.ContentVersion
                ),
            DocumentCacheWriterCandidateMetadataComparison.NotSupplied => throw new InvalidOperationException(
                "Supplied candidates cannot be marked NotSupplied."
            ),
            _ => throw new InvalidOperationException(
                "Unsupported cache-writer candidate metadata comparison."
            ),
        };
    }

    private static DocumentCacheWriterClassificationSelection SelectPendingProjectionAction(
        long sourceContentVersion,
        DocumentCacheWriterCandidateObservation candidateObservation
    )
    {
        if (candidateObservation.Candidate is null)
        {
            return DocumentCacheWriterClassificationSelection.NeedsMaterialization(sourceContentVersion);
        }

        if (candidateObservation.Candidate.ContentVersion != sourceContentVersion)
        {
            return DocumentCacheWriterClassificationSelection.StaleCandidateSuppressed(
                sourceContentVersion,
                candidateObservation.Candidate.ContentVersion
            );
        }

        return DocumentCacheWriterClassificationSelection.WriteCandidateThenAcknowledgeWork(
            candidateObservation.Candidate,
            sourceContentVersion
        );
    }

    private static bool CacheIsAbsentOrBehind(long? cacheContentVersion, long sourceContentVersion) =>
        cacheContentVersion is null || cacheContentVersion < sourceContentVersion;
}
