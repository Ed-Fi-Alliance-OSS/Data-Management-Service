// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Backend;

internal enum DocumentCacheAdministrativeStateLockMode
{
    Shared = 1,
    Exclusive = 2,
}

internal sealed record DocumentCacheAdministrativeLifecycleTransitionRequest
{
    public DocumentCacheAdministrativeLifecycleTransitionRequest(
        DocumentCacheLifecycleState expectedLifecycle,
        bool expectedCacheAheadRecoveryRequired,
        DocumentCacheLifecycleState nextLifecycle,
        bool nextCacheAheadRecoveryRequired
    )
    {
        ExpectedLifecycle = RequireDefined(expectedLifecycle, nameof(expectedLifecycle));
        ExpectedCacheAheadRecoveryRequired = expectedCacheAheadRecoveryRequired;
        NextLifecycle = RequireDefined(nextLifecycle, nameof(nextLifecycle));
        NextCacheAheadRecoveryRequired = nextCacheAheadRecoveryRequired;
    }

    public DocumentCacheLifecycleState ExpectedLifecycle { get; }

    public bool ExpectedCacheAheadRecoveryRequired { get; }

    public DocumentCacheLifecycleState NextLifecycle { get; }

    public bool NextCacheAheadRecoveryRequired { get; }

    private static DocumentCacheLifecycleState RequireDefined(
        DocumentCacheLifecycleState lifecycle,
        string parameterName
    ) =>
        Enum.IsDefined(lifecycle)
            ? lifecycle
            : throw new ArgumentOutOfRangeException(parameterName, lifecycle, "Unsupported lifecycle.");
}

internal enum DocumentCacheAdministrativeLifecycleTransitionStatus
{
    Transitioned = 1,
    NotTransitioned = 2,
}

internal sealed record DocumentCacheAdministrativeLifecycleTransitionResult
{
    private DocumentCacheAdministrativeLifecycleTransitionResult(
        DocumentCacheAdministrativeLifecycleTransitionStatus status,
        DocumentCacheLifecycleReadResult lifecycleReadResult,
        string message
    )
    {
        if (
            status == DocumentCacheAdministrativeLifecycleTransitionStatus.Transitioned
            && !lifecycleReadResult.Succeeded
        )
        {
            throw new ArgumentException("Transitioned results require the post-transition lifecycle.");
        }

        Status = status;
        LifecycleReadResult =
            lifecycleReadResult ?? throw new ArgumentNullException(nameof(lifecycleReadResult));
        Message = DocumentCacheAdministrativePrimitiveText.Sanitize(message);
    }

    public DocumentCacheAdministrativeLifecycleTransitionStatus Status { get; }

    public DocumentCacheLifecycleReadResult LifecycleReadResult { get; }

    public string Message { get; }

    public bool Mutated => Status == DocumentCacheAdministrativeLifecycleTransitionStatus.Transitioned;

    public static DocumentCacheAdministrativeLifecycleTransitionResult Transitioned(
        DocumentCacheLifecycleObservation lifecycle
    )
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        return new(
            DocumentCacheAdministrativeLifecycleTransitionStatus.Transitioned,
            DocumentCacheLifecycleReadResult.Success(lifecycle),
            "DocumentCache lifecycle transition completed."
        );
    }

    public static DocumentCacheAdministrativeLifecycleTransitionResult NotTransitioned(
        DocumentCacheLifecycleReadResult lifecycleReadResult
    )
    {
        ArgumentNullException.ThrowIfNull(lifecycleReadResult);

        return new(
            DocumentCacheAdministrativeLifecycleTransitionStatus.NotTransitioned,
            lifecycleReadResult,
            "DocumentCache lifecycle transition did not match the expected lifecycle or latch state."
        );
    }
}

internal sealed record DocumentCacheAdministrativeActivationTransitionResult
{
    public DocumentCacheAdministrativeActivationTransitionResult(
        DocumentCacheProviderPrerequisiteValidationResult activationPrerequisites,
        DocumentCacheAdministrativeLifecycleTransitionResult transition,
        string message
    )
    {
        ActivationPrerequisites =
            activationPrerequisites ?? throw new ArgumentNullException(nameof(activationPrerequisites));
        Transition = transition ?? throw new ArgumentNullException(nameof(transition));
        Message = DocumentCacheAdministrativePrimitiveText.Sanitize(message);
    }

    public DocumentCacheProviderPrerequisiteValidationResult ActivationPrerequisites { get; }

    public DocumentCacheAdministrativeLifecycleTransitionResult Transition { get; }

    public string Message { get; }

    public bool Mutated => Transition.Mutated;
}

internal enum DocumentCacheAdministrativeClearTarget
{
    DocumentCache = 1,
    DocumentProjectionWork = 2,
}

internal sealed record DocumentCacheAdministrativeClearBatchRequest
{
    public DocumentCacheAdministrativeClearBatchRequest(int pageSize)
    {
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                "DocumentCache administrative clear batch size must be positive."
            );
        }

        PageSize = pageSize;
    }

    public int PageSize { get; }
}

internal sealed record DocumentCacheAdministrativeClearBatchResult
{
    public DocumentCacheAdministrativeClearBatchResult(
        DocumentCacheAdministrativeClearTarget target,
        int pageSize,
        ImmutableArray<long> clearedDocumentIds,
        string message
    )
    {
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported clear target.");
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                "DocumentCache administrative clear batch size must be positive."
            );
        }

        if (!clearedDocumentIds.IsDefaultOrEmpty && clearedDocumentIds.Length > pageSize)
        {
            throw new ArgumentException(
                "Cleared document diagnostics cannot exceed the bounded clear batch size.",
                nameof(clearedDocumentIds)
            );
        }

        Target = target;
        PageSize = pageSize;
        ClearedDocumentIds = clearedDocumentIds.IsDefault ? [] : clearedDocumentIds.Sort();
        Message = DocumentCacheAdministrativePrimitiveText.Sanitize(message);
    }

    public DocumentCacheAdministrativeClearTarget Target { get; }

    public int PageSize { get; }

    public ImmutableArray<long> ClearedDocumentIds { get; }

    public int RowsCleared => ClearedDocumentIds.Length;

    public bool Mutated => RowsCleared > 0;

    public bool FilledBatch => RowsCleared == PageSize;

    public string Message { get; }
}

internal sealed record DocumentCacheAdministrativeProjectedStateEmptinessResult
{
    public DocumentCacheAdministrativeProjectedStateEmptinessResult(
        bool documentCacheEmpty,
        bool documentProjectionWorkEmpty,
        string message
    )
    {
        DocumentCacheEmpty = documentCacheEmpty;
        DocumentProjectionWorkEmpty = documentProjectionWorkEmpty;
        Message = DocumentCacheAdministrativePrimitiveText.Sanitize(message);
    }

    public bool DocumentCacheEmpty { get; }

    public bool DocumentProjectionWorkEmpty { get; }

    public bool CacheAndWorkEmpty => DocumentCacheEmpty && DocumentProjectionWorkEmpty;

    public string Message { get; }
}

internal sealed record DocumentCacheAdministrativeBaselineBoundaryResult
{
    public DocumentCacheAdministrativeBaselineBoundaryResult(long? boundaryDocumentId, string message)
    {
        if (boundaryDocumentId is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundaryDocumentId),
                boundaryDocumentId,
                "Baseline boundary document id must be positive when present."
            );
        }

        BoundaryDocumentId = boundaryDocumentId;
        Message = DocumentCacheAdministrativePrimitiveText.Sanitize(message);
    }

    public long? BoundaryDocumentId { get; }

    public bool HasDocuments => BoundaryDocumentId is not null;

    public string Message { get; }
}

internal sealed record DocumentCacheAdministrativeWorkHighWaterObservationRequest
{
    public DocumentCacheAdministrativeWorkHighWaterObservationRequest(
        int highWaterMark,
        int diagnosticCapacity
    )
    {
        if (highWaterMark <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(highWaterMark),
                highWaterMark,
                "Baseline high-water mark must be positive."
            );
        }

        if (diagnosticCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(diagnosticCapacity),
                diagnosticCapacity,
                "Baseline high-water diagnostic capacity must be positive."
            );
        }

        HighWaterMark = highWaterMark;
        DiagnosticCapacity = diagnosticCapacity;
    }

    public int HighWaterMark { get; }

    public int DiagnosticCapacity { get; }

    public int HighWaterPlusOne => checked(HighWaterMark + 1);
}

internal sealed record DocumentCacheAdministrativeWorkHighWaterObservationResult
{
    public DocumentCacheAdministrativeWorkHighWaterObservationResult(
        int highWaterMark,
        int observedWorkRows,
        ImmutableArray<long> diagnosticDocumentIds,
        string message
    )
    {
        if (highWaterMark <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(highWaterMark),
                highWaterMark,
                "Baseline high-water mark must be positive."
            );
        }

        if (observedWorkRows < 0 || observedWorkRows > checked(highWaterMark + 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedWorkRows),
                observedWorkRows,
                "Observed durable work rows must be bounded by high-water plus one."
            );
        }

        if (!diagnosticDocumentIds.IsDefaultOrEmpty && diagnosticDocumentIds.Any(id => id <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(diagnosticDocumentIds),
                "Diagnostic document ids must be positive."
            );
        }

        if (!diagnosticDocumentIds.IsDefaultOrEmpty && diagnosticDocumentIds.Length > observedWorkRows)
        {
            throw new ArgumentException(
                "High-water diagnostics cannot contain more document ids than observed work rows.",
                nameof(diagnosticDocumentIds)
            );
        }

        HighWaterMark = highWaterMark;
        ObservedWorkRows = observedWorkRows;
        DiagnosticDocumentIds = diagnosticDocumentIds.IsDefault
            ? []
            : diagnosticDocumentIds.Take(observedWorkRows).ToImmutableArray();
        Message = DocumentCacheAdministrativePrimitiveText.Sanitize(message);
    }

    public int HighWaterMark { get; }

    public int ObservedWorkRows { get; }

    public ImmutableArray<long> DiagnosticDocumentIds { get; }

    public bool IsAtOrAboveHighWater => ObservedWorkRows >= HighWaterMark;

    public string Message { get; }
}

internal sealed record DocumentCacheAdministrativeBaselineSeedPageRequest
{
    public DocumentCacheAdministrativeBaselineSeedPageRequest(
        long boundaryDocumentId,
        long afterDocumentId,
        int pageSize
    )
    {
        if (boundaryDocumentId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundaryDocumentId),
                boundaryDocumentId,
                "Baseline boundary document id must be positive."
            );
        }

        if (afterDocumentId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(afterDocumentId),
                afterDocumentId,
                "Baseline cursor document id cannot be negative."
            );
        }

        if (afterDocumentId >= boundaryDocumentId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(afterDocumentId),
                afterDocumentId,
                "Baseline cursor document id must be below the captured boundary."
            );
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                "Baseline seed page size must be positive."
            );
        }

        BoundaryDocumentId = boundaryDocumentId;
        AfterDocumentId = afterDocumentId;
        PageSize = pageSize;
    }

    public long BoundaryDocumentId { get; }

    public long AfterDocumentId { get; }

    public int PageSize { get; }
}

internal enum DocumentCacheAdministrativeBaselineWorkMutationKind
{
    None = 1,
    Inserted = 2,
    Advanced = 3,
    Lowered = 4,
    Retry = 5,
}

internal sealed record DocumentCacheAdministrativeBaselineSeededDocument
{
    public DocumentCacheAdministrativeBaselineSeededDocument(
        long documentId,
        long sourceContentVersion,
        long? previousRequiredContentVersion,
        DocumentCacheAdministrativeBaselineWorkMutationKind mutationKind
    )
    {
        if (documentId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(documentId),
                documentId,
                "Baseline seed document id must be positive."
            );
        }

        if (sourceContentVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceContentVersion),
                sourceContentVersion,
                "Source content version cannot be negative."
            );
        }

        if (previousRequiredContentVersion is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(previousRequiredContentVersion),
                previousRequiredContentVersion,
                "Previous required content version cannot be negative."
            );
        }

        if (!Enum.IsDefined(mutationKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mutationKind),
                mutationKind,
                "Unsupported baseline work mutation kind."
            );
        }

        DocumentId = documentId;
        SourceContentVersion = sourceContentVersion;
        PreviousRequiredContentVersion = previousRequiredContentVersion;
        MutationKind = mutationKind;
    }

    public long DocumentId { get; }

    public long SourceContentVersion { get; }

    public long? PreviousRequiredContentVersion { get; }

    public DocumentCacheAdministrativeBaselineWorkMutationKind MutationKind { get; }

    public bool Mutated =>
        MutationKind
            is DocumentCacheAdministrativeBaselineWorkMutationKind.Inserted
                or DocumentCacheAdministrativeBaselineWorkMutationKind.Advanced
                or DocumentCacheAdministrativeBaselineWorkMutationKind.Lowered;

    public bool RequiresRetry => MutationKind == DocumentCacheAdministrativeBaselineWorkMutationKind.Retry;
}

internal enum DocumentCacheAdministrativeBaselineSeedPageStatus
{
    PageSeeded = 1,
    Empty = 2,
    RetryFromLastCommittedKey = 3,
}

internal sealed record DocumentCacheAdministrativeBaselineSeedPageResult
{
    public DocumentCacheAdministrativeBaselineSeedPageResult(
        DocumentCacheAdministrativeBaselineSeedPageStatus status,
        long boundaryDocumentId,
        long afterDocumentId,
        int pageSize,
        ImmutableArray<DocumentCacheAdministrativeBaselineSeededDocument> documents,
        string message
    )
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported seed page status.");
        }

        if (boundaryDocumentId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundaryDocumentId),
                boundaryDocumentId,
                "Baseline boundary document id must be positive."
            );
        }

        if (afterDocumentId < 0 || afterDocumentId >= boundaryDocumentId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(afterDocumentId),
                afterDocumentId,
                "Baseline cursor document id must be non-negative and below the captured boundary."
            );
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                "Baseline seed page size must be positive."
            );
        }

        ImmutableArray<DocumentCacheAdministrativeBaselineSeededDocument> materializedDocuments =
            documents.IsDefault ? [] : documents;

        if (materializedDocuments.Length > pageSize)
        {
            throw new ArgumentException("Baseline seed page cannot contain more rows than PageSize.");
        }

        if (
            materializedDocuments.Any(document =>
                document.DocumentId <= afterDocumentId || document.DocumentId > boundaryDocumentId
            )
        )
        {
            throw new ArgumentException(
                "Baseline seed page documents must be within the requested keyset boundary.",
                nameof(documents)
            );
        }

        if (
            !materializedDocuments
                .Select(document => document.DocumentId)
                .Order()
                .SequenceEqual(materializedDocuments.Select(document => document.DocumentId))
        )
        {
            throw new ArgumentException(
                "Baseline seed page documents must be ordered by DocumentId.",
                nameof(documents)
            );
        }

        if (
            status == DocumentCacheAdministrativeBaselineSeedPageStatus.Empty
            && !materializedDocuments.IsEmpty
        )
        {
            throw new ArgumentException("Empty seed page results cannot contain documents.");
        }

        if (
            status == DocumentCacheAdministrativeBaselineSeedPageStatus.PageSeeded
            && materializedDocuments.Any(document => document.RequiresRetry)
        )
        {
            throw new ArgumentException("Seeded page results cannot contain retry documents.");
        }

        if (
            status == DocumentCacheAdministrativeBaselineSeedPageStatus.RetryFromLastCommittedKey
            && !materializedDocuments.Any(document => document.RequiresRetry)
        )
        {
            throw new ArgumentException("Retry seed page results require a retry document.");
        }

        Status = status;
        BoundaryDocumentId = boundaryDocumentId;
        AfterDocumentId = afterDocumentId;
        PageSize = pageSize;
        Documents = materializedDocuments;
        Message = DocumentCacheAdministrativePrimitiveText.Sanitize(message);
    }

    public DocumentCacheAdministrativeBaselineSeedPageStatus Status { get; }

    public long BoundaryDocumentId { get; }

    public long AfterDocumentId { get; }

    public int PageSize { get; }

    public ImmutableArray<DocumentCacheAdministrativeBaselineSeededDocument> Documents { get; }

    public int RowsVisited => Documents.Length;

    public int WorkMutationCount => Documents.Count(document => document.Mutated);

    public bool Mutated => WorkMutationCount > 0;

    public bool FilledPage => RowsVisited == PageSize;

    public long? LastVisitedDocumentId => Documents.IsEmpty ? null : Documents[^1].DocumentId;

    public ImmutableArray<long> AffectedDocumentIds =>
        Documents
            .Where(document => document.Mutated || document.RequiresRetry)
            .Select(document => document.DocumentId)
            .Take(PageSize)
            .ToImmutableArray();

    public string Message { get; }
}

internal sealed record DocumentCacheAdministrativeScrubPageRequest
{
    public DocumentCacheAdministrativeScrubPageRequest(
        long boundaryDocumentId,
        long afterDocumentId,
        int pageSize
    )
    {
        if (boundaryDocumentId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundaryDocumentId),
                boundaryDocumentId,
                "Scrub boundary document id must be positive."
            );
        }

        if (afterDocumentId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(afterDocumentId),
                afterDocumentId,
                "Scrub cursor document id cannot be negative."
            );
        }

        if (afterDocumentId >= boundaryDocumentId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(afterDocumentId),
                afterDocumentId,
                "Scrub cursor document id must be below the captured boundary."
            );
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                "Scrub page size must be positive."
            );
        }

        BoundaryDocumentId = boundaryDocumentId;
        AfterDocumentId = afterDocumentId;
        PageSize = pageSize;
    }

    public long BoundaryDocumentId { get; }

    public long AfterDocumentId { get; }

    public int PageSize { get; }
}

internal enum DocumentCacheAdministrativeScrubMutationKind
{
    None = 1,
    Inserted = 2,
    Advanced = 3,
    Lowered = 4,
    CacheAheadLatchSet = 5,
    Retry = 6,
}

internal sealed record DocumentCacheAdministrativeScrubbedDocument
{
    public DocumentCacheAdministrativeScrubbedDocument(
        long documentId,
        long sourceContentVersion,
        long? cacheContentVersion,
        long? previousRequiredContentVersion,
        DocumentCacheAdministrativeScrubMutationKind mutationKind
    )
    {
        if (documentId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(documentId),
                documentId,
                "Scrub document id must be positive."
            );
        }

        if (sourceContentVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceContentVersion),
                sourceContentVersion,
                "Source content version cannot be negative."
            );
        }

        if (cacheContentVersion is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cacheContentVersion),
                cacheContentVersion,
                "Cache content version cannot be negative."
            );
        }

        if (previousRequiredContentVersion is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(previousRequiredContentVersion),
                previousRequiredContentVersion,
                "Previous required content version cannot be negative."
            );
        }

        if (!Enum.IsDefined(mutationKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mutationKind),
                mutationKind,
                "Unsupported scrub mutation kind."
            );
        }

        DocumentId = documentId;
        SourceContentVersion = sourceContentVersion;
        CacheContentVersion = cacheContentVersion;
        PreviousRequiredContentVersion = previousRequiredContentVersion;
        MutationKind = mutationKind;
    }

    public long DocumentId { get; }

    public long SourceContentVersion { get; }

    public long? CacheContentVersion { get; }

    public long? PreviousRequiredContentVersion { get; }

    public DocumentCacheAdministrativeScrubMutationKind MutationKind { get; }

    public bool WorkMutated =>
        MutationKind
            is DocumentCacheAdministrativeScrubMutationKind.Inserted
                or DocumentCacheAdministrativeScrubMutationKind.Advanced
                or DocumentCacheAdministrativeScrubMutationKind.Lowered;

    public bool LatchSet => MutationKind == DocumentCacheAdministrativeScrubMutationKind.CacheAheadLatchSet;

    public bool RequiresRetry => MutationKind == DocumentCacheAdministrativeScrubMutationKind.Retry;

    public bool Mutated => WorkMutated || LatchSet;
}

internal enum DocumentCacheAdministrativeScrubPageStatus
{
    PageScrubbed = 1,
    Empty = 2,
    CacheAheadLatched = 3,
    RetryFromLastCommittedKey = 4,
}

internal sealed record DocumentCacheAdministrativeScrubPageResult
{
    public DocumentCacheAdministrativeScrubPageResult(
        DocumentCacheAdministrativeScrubPageStatus status,
        long boundaryDocumentId,
        long afterDocumentId,
        int pageSize,
        ImmutableArray<DocumentCacheAdministrativeScrubbedDocument> documents,
        string message
    )
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported scrub page status.");
        }

        if (boundaryDocumentId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundaryDocumentId),
                boundaryDocumentId,
                "Scrub boundary document id must be positive."
            );
        }

        if (afterDocumentId < 0 || afterDocumentId >= boundaryDocumentId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(afterDocumentId),
                afterDocumentId,
                "Scrub cursor document id must be non-negative and below the captured boundary."
            );
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                "Scrub page size must be positive."
            );
        }

        ImmutableArray<DocumentCacheAdministrativeScrubbedDocument> materializedDocuments =
            documents.IsDefault ? [] : documents;

        if (materializedDocuments.Length > pageSize)
        {
            throw new ArgumentException("Scrub page cannot contain more rows than PageSize.");
        }

        if (
            materializedDocuments.Any(document =>
                document.DocumentId <= afterDocumentId || document.DocumentId > boundaryDocumentId
            )
        )
        {
            throw new ArgumentException(
                "Scrub page documents must be within the requested keyset boundary.",
                nameof(documents)
            );
        }

        if (
            !materializedDocuments
                .Select(document => document.DocumentId)
                .Order()
                .SequenceEqual(materializedDocuments.Select(document => document.DocumentId))
        )
        {
            throw new ArgumentException(
                "Scrub page documents must be ordered by DocumentId.",
                nameof(documents)
            );
        }

        if (status == DocumentCacheAdministrativeScrubPageStatus.Empty && !materializedDocuments.IsEmpty)
        {
            throw new ArgumentException("Empty scrub page results cannot contain documents.");
        }

        if (
            status == DocumentCacheAdministrativeScrubPageStatus.PageScrubbed
            && materializedDocuments.Any(document => document.RequiresRetry || document.LatchSet)
        )
        {
            throw new ArgumentException("Scrubbed page results cannot contain retry or latch-set documents.");
        }

        if (
            status == DocumentCacheAdministrativeScrubPageStatus.CacheAheadLatched
            && !materializedDocuments.Any(document => document.LatchSet)
        )
        {
            throw new ArgumentException("Cache-ahead scrub page results require a latch-set document.");
        }

        if (
            status == DocumentCacheAdministrativeScrubPageStatus.RetryFromLastCommittedKey
            && !materializedDocuments.Any(document => document.RequiresRetry)
        )
        {
            throw new ArgumentException("Retry scrub page results require a retry document.");
        }

        Status = status;
        BoundaryDocumentId = boundaryDocumentId;
        AfterDocumentId = afterDocumentId;
        PageSize = pageSize;
        Documents = materializedDocuments;
        Message = DocumentCacheAdministrativePrimitiveText.Sanitize(message);
    }

    public DocumentCacheAdministrativeScrubPageStatus Status { get; }

    public long BoundaryDocumentId { get; }

    public long AfterDocumentId { get; }

    public int PageSize { get; }

    public ImmutableArray<DocumentCacheAdministrativeScrubbedDocument> Documents { get; }

    public int RowsVisited => Documents.Length;

    public int WorkMutationCount => Documents.Count(document => document.WorkMutated);

    public bool LatchSet => Documents.Any(document => document.LatchSet);

    public bool Mutated => Documents.Any(document => document.Mutated);

    public bool FilledPage => RowsVisited == PageSize;

    public long? LastVisitedDocumentId => Documents.IsEmpty ? null : Documents[^1].DocumentId;

    public ImmutableArray<long> AffectedDocumentIds =>
        Documents
            .Where(document => document.Mutated || document.RequiresRetry)
            .Select(document => document.DocumentId)
            .Take(PageSize)
            .ToImmutableArray();

    public string Message { get; }
}

internal sealed record DocumentCacheAdministrativeWorkClearance
{
    private DocumentCacheAdministrativeWorkClearance(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheDownstreamPublicationStatus downstreamPublicationStatus,
        DocumentCacheOfflineWriterAdmissionConfirmation offlineWriterAdmissionConfirmation
    )
    {
        Command = command;
        DownstreamPublicationStatus = downstreamPublicationStatus;
        OfflineWriterAdmissionConfirmation = offlineWriterAdmissionConfirmation;
    }

    public DocumentCacheAdministrativeCommand Command { get; }

    public DocumentCacheDownstreamPublicationStatus DownstreamPublicationStatus { get; }

    public DocumentCacheOfflineWriterAdmissionConfirmation OfflineWriterAdmissionConfirmation { get; }

    public static DocumentCacheAdministrativeWorkClearance Require(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheDownstreamPublicationStatus downstreamPublicationStatus,
        DocumentCacheOfflineWriterAdmissionConfirmation? offlineWriterAdmissionConfirmation
    )
    {
        if (!Enum.IsDefined(command))
        {
            throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported command.");
        }

        if (!Enum.IsDefined(downstreamPublicationStatus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(downstreamPublicationStatus),
                downstreamPublicationStatus,
                "Unsupported downstream publication status."
            );
        }

        if (downstreamPublicationStatus != DocumentCacheDownstreamPublicationStatus.InternalOnly)
        {
            throw new InvalidOperationException(
                "DocumentProjectionWork clearing requires trusted internal-only downstream-publication proof."
            );
        }

        DocumentCacheOfflineWriterAdmissionConfirmation expectedConfirmation = command switch
        {
            DocumentCacheAdministrativeCommand.OfflineActivation =>
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained,
            DocumentCacheAdministrativeCommand.OfflineDeactivation =>
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained,
            DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery =>
                DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained,
            _ => throw new InvalidOperationException(
                "DocumentProjectionWork clearing is available only to offline activation, offline deactivation, and internal-only cache-ahead recovery workflows."
            ),
        };

        if (offlineWriterAdmissionConfirmation != expectedConfirmation)
        {
            throw new InvalidOperationException(
                "DocumentProjectionWork clearing requires the command-specific offline writer-admission confirmation."
            );
        }

        return new(command, downstreamPublicationStatus, expectedConfirmation);
    }
}

internal interface IDocumentCacheAdministrativePrimitives
{
    RelationalProviderToken ProviderToken { get; }

    Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeStateLockMode lockMode = DocumentCacheAdministrativeStateLockMode.Shared,
        CancellationToken cancellationToken = default
    );

    Task LockCanonicalDocumentsForGuardedActivationAsync(
        IRelationalWriteSession mutexSession,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheGuardedNewEmptyActivationState> ReadGuardedNewEmptyActivationStateAsync(
        IRelationalWriteSession mutexSession,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPrerequisitesAsync(
        IRelationalWriteSession mutexSession,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheAdministrativeLifecycleTransitionResult> TryTransitionLifecycleAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeLifecycleTransitionRequest request,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheAdministrativeActivationTransitionResult> TryTransitionLifecycleAfterActivationPrerequisitesAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeLifecycleTransitionRequest request,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentCacheBatchAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeClearBatchRequest request,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentProjectionWorkBatchAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeClearBatchRequest request,
        DocumentCacheAdministrativeWorkClearance clearance,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheAdministrativeProjectedStateEmptinessResult> ReadProjectedStateEmptinessAsync(
        IRelationalWriteSession mutexSession,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheAdministrativeBaselineBoundaryResult> CaptureBaselineBoundaryAsync(
        IRelationalWriteSession mutexSession,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheAdministrativeWorkHighWaterObservationResult> ObserveWorkHighWaterAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeWorkHighWaterObservationRequest request,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheAdministrativeBaselineSeedPageResult> SeedBaselinePageAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeBaselineSeedPageRequest request,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheAdministrativeScrubPageResult> ScrubPageAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeScrubPageRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed record DocumentCacheAdministrativePrimitiveCommands
{
    public DocumentCacheAdministrativePrimitiveCommands(
        RelationalProviderToken providerToken,
        string sharedLifecycleObservationCommandText,
        string exclusiveLifecycleObservationCommandText,
        string guardedActivationDocumentLockCommandText,
        string guardedActivationEmptyStateCommandText,
        string transitionLifecycleCommandText,
        string clearDocumentCacheBatchCommandText,
        string clearDocumentProjectionWorkBatchCommandText,
        string projectedStateEmptinessCommandText,
        string captureBaselineBoundaryCommandText,
        string observeWorkHighWaterCommandText,
        string seedBaselinePageCommandText,
        string scrubPageCommandText,
        string? activationPrerequisiteCommandText,
        DocumentCacheLifecycleReaderQuery lifecycleReaderQuery
    )
    {
        ProviderToken = providerToken ?? throw new ArgumentNullException(nameof(providerToken));
        SharedLifecycleObservationCommandText = RequireCommandText(
            sharedLifecycleObservationCommandText,
            nameof(sharedLifecycleObservationCommandText)
        );
        ExclusiveLifecycleObservationCommandText = RequireCommandText(
            exclusiveLifecycleObservationCommandText,
            nameof(exclusiveLifecycleObservationCommandText)
        );
        GuardedActivationDocumentLockCommandText = RequireCommandText(
            guardedActivationDocumentLockCommandText,
            nameof(guardedActivationDocumentLockCommandText)
        );
        GuardedActivationEmptyStateCommandText = RequireCommandText(
            guardedActivationEmptyStateCommandText,
            nameof(guardedActivationEmptyStateCommandText)
        );
        TransitionLifecycleCommandText = RequireCommandText(
            transitionLifecycleCommandText,
            nameof(transitionLifecycleCommandText)
        );
        ClearDocumentCacheBatchCommandText = RequireCommandText(
            clearDocumentCacheBatchCommandText,
            nameof(clearDocumentCacheBatchCommandText)
        );
        ClearDocumentProjectionWorkBatchCommandText = RequireCommandText(
            clearDocumentProjectionWorkBatchCommandText,
            nameof(clearDocumentProjectionWorkBatchCommandText)
        );
        ProjectedStateEmptinessCommandText = RequireCommandText(
            projectedStateEmptinessCommandText,
            nameof(projectedStateEmptinessCommandText)
        );
        CaptureBaselineBoundaryCommandText = RequireCommandText(
            captureBaselineBoundaryCommandText,
            nameof(captureBaselineBoundaryCommandText)
        );
        ObserveWorkHighWaterCommandText = RequireCommandText(
            observeWorkHighWaterCommandText,
            nameof(observeWorkHighWaterCommandText)
        );
        SeedBaselinePageCommandText = RequireCommandText(
            seedBaselinePageCommandText,
            nameof(seedBaselinePageCommandText)
        );
        ScrubPageCommandText = RequireCommandText(scrubPageCommandText, nameof(scrubPageCommandText));
        ActivationPrerequisiteCommandText = activationPrerequisiteCommandText;
        LifecycleReaderQuery =
            lifecycleReaderQuery ?? throw new ArgumentNullException(nameof(lifecycleReaderQuery));
    }

    public RelationalProviderToken ProviderToken { get; }

    public string SharedLifecycleObservationCommandText { get; }

    public string ExclusiveLifecycleObservationCommandText { get; }

    public string GuardedActivationDocumentLockCommandText { get; }

    public string GuardedActivationEmptyStateCommandText { get; }

    public string TransitionLifecycleCommandText { get; }

    public string ClearDocumentCacheBatchCommandText { get; }

    public string ClearDocumentProjectionWorkBatchCommandText { get; }

    public string ProjectedStateEmptinessCommandText { get; }

    public string CaptureBaselineBoundaryCommandText { get; }

    public string ObserveWorkHighWaterCommandText { get; }

    public string SeedBaselinePageCommandText { get; }

    public string ScrubPageCommandText { get; }

    public string? ActivationPrerequisiteCommandText { get; }

    public DocumentCacheLifecycleReaderQuery LifecycleReaderQuery { get; }

    public string GetLifecycleObservationCommandText(DocumentCacheAdministrativeStateLockMode lockMode) =>
        lockMode switch
        {
            DocumentCacheAdministrativeStateLockMode.Shared => SharedLifecycleObservationCommandText,
            DocumentCacheAdministrativeStateLockMode.Exclusive => ExclusiveLifecycleObservationCommandText,
            _ => throw new ArgumentOutOfRangeException(nameof(lockMode), lockMode, "Unsupported lock mode."),
        };

    private static string RequireCommandText(string commandText, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            throw new ArgumentException("Command text is required.", parameterName);
        }

        return commandText;
    }
}

internal static class DocumentCacheAdministrativePrimitivesSupport
{
    private const string CanonicalDocumentsEmptyColumnName = "CanonicalDocumentsEmpty";
    private const string DocumentCacheEmptyColumnName = "DocumentCacheEmpty";
    private const string DocumentProjectionWorkEmptyColumnName = "DocumentProjectionWorkEmpty";
    private const string ClearedDocumentIdColumnName = "DocumentId";
    private const string BoundaryDocumentIdColumnName = "BoundaryDocumentId";
    private const string SourceContentVersionColumnName = "SourceContentVersion";
    private const string CacheContentVersionColumnName = "CacheContentVersion";
    private const string PreviousRequiredContentVersionColumnName = "PreviousRequiredContentVersion";
    private const string MutationKindColumnName = "MutationKind";
    private const string ReadCommittedSnapshotColumnName = "ReadCommittedSnapshot";
    private const string NestedTriggersColumnName = "NestedTriggers";

    private static readonly DocumentCacheAdministrativePrimitiveCommands _pgsqlCommands = CreateCommands(
        SqlDialect.Pgsql,
        RelationalProviderToken.Postgresql
    );

    private static readonly DocumentCacheAdministrativePrimitiveCommands _mssqlCommands = CreateCommands(
        SqlDialect.Mssql,
        RelationalProviderToken.SqlServer
    );

    public static DocumentCacheAdministrativePrimitiveCommands GetCommands(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => _pgsqlCommands,
            SqlDialect.Mssql => _mssqlCommands,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

    public static Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        DocumentCacheAdministrativeStateLockMode lockMode,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);

        return ReadLifecycleAsync(
            mutexSession.CreateCommandExecutor(),
            commands,
            lockMode,
            cancellationToken
        );
    }

    public static Task LockCanonicalDocumentsForGuardedActivationAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);

        return ExecuteNoResultAsync(
            mutexSession.CreateCommandExecutor(),
            new RelationalCommand(commands.GuardedActivationDocumentLockCommandText),
            cancellationToken
        );
    }

    public static async Task<DocumentCacheGuardedNewEmptyActivationState> ReadGuardedNewEmptyActivationStateAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);

        return await mutexSession
            .CreateCommandExecutor()
            .ExecuteReaderAsync(
                new RelationalCommand(commands.GuardedActivationEmptyStateCommandText),
                static async (reader, readerCancellationToken) =>
                {
                    if (!await reader.ReadAsync(readerCancellationToken).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            "Guarded new-empty activation state observation did not return a row."
                        );
                    }

                    bool canonicalDocumentsEmpty = ReadRequiredBoolean(
                        reader,
                        CanonicalDocumentsEmptyColumnName
                    );
                    bool documentCacheEmpty = ReadRequiredBoolean(reader, DocumentCacheEmptyColumnName);
                    bool documentProjectionWorkEmpty = ReadRequiredBoolean(
                        reader,
                        DocumentProjectionWorkEmptyColumnName
                    );

                    return new DocumentCacheGuardedNewEmptyActivationState(
                        canonicalDocumentsEmpty,
                        documentCacheEmpty,
                        documentProjectionWorkEmpty,
                        canonicalDocumentsEmpty && documentCacheEmpty && documentProjectionWorkEmpty
                            ? "Guarded new-empty state observed."
                            : "Guarded new-empty activation requires empty canonical documents, cache rows, and durable work."
                    );
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public static Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPrerequisitesAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);

        if (commands.ActivationPrerequisiteCommandText is null)
        {
            return Task.FromResult(
                DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
                    DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
                )
            );
        }

        return ValidateSqlServerActivationPrerequisitesAsync(
            mutexSession.CreateCommandExecutor(),
            commands.ActivationPrerequisiteCommandText,
            cancellationToken
        );
    }

    public static async Task<DocumentCacheAdministrativeLifecycleTransitionResult> TryTransitionLifecycleAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        DocumentCacheAdministrativeLifecycleTransitionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(request);

        DocumentCacheLifecycleReadResult transitionReadResult = await mutexSession
            .CreateCommandExecutor()
            .ExecuteReaderAsync(
                new RelationalCommand(
                    commands.TransitionLifecycleCommandText,
                    CreateTransitionParameters(request)
                ),
                (reader, readerCancellationToken) =>
                    ReadLifecycleAsync(reader, commands.LifecycleReaderQuery, readerCancellationToken),
                cancellationToken
            )
            .ConfigureAwait(false);

        if (transitionReadResult.Succeeded)
        {
            return DocumentCacheAdministrativeLifecycleTransitionResult.Transitioned(
                transitionReadResult.Lifecycle!
            );
        }

        DocumentCacheLifecycleReadResult currentLifecycle = await ReadLifecycleAsync(
                mutexSession.CreateCommandExecutor(),
                commands,
                DocumentCacheAdministrativeStateLockMode.Exclusive,
                cancellationToken
            )
            .ConfigureAwait(false);

        return DocumentCacheAdministrativeLifecycleTransitionResult.NotTransitioned(currentLifecycle);
    }

    public static async Task<DocumentCacheAdministrativeActivationTransitionResult> TryTransitionLifecycleAfterActivationPrerequisitesAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        DocumentCacheAdministrativeLifecycleTransitionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(request);

        DocumentCacheProviderPrerequisiteValidationResult activationPrerequisites =
            await ValidateActivationPrerequisitesAsync(mutexSession, commands, cancellationToken)
                .ConfigureAwait(false);

        if (!activationPrerequisites.IsSatisfied)
        {
            DocumentCacheLifecycleReadResult currentLifecycle = await ReadLifecycleAsync(
                    mutexSession.CreateCommandExecutor(),
                    commands,
                    DocumentCacheAdministrativeStateLockMode.Exclusive,
                    cancellationToken
                )
                .ConfigureAwait(false);

            return new DocumentCacheAdministrativeActivationTransitionResult(
                activationPrerequisites,
                DocumentCacheAdministrativeLifecycleTransitionResult.NotTransitioned(currentLifecycle),
                "Activation prerequisite validation failed before lifecycle mutation."
            );
        }

        DocumentCacheAdministrativeLifecycleTransitionResult transition = await TryTransitionLifecycleAsync(
                mutexSession,
                commands,
                request,
                cancellationToken
            )
            .ConfigureAwait(false);

        return new DocumentCacheAdministrativeActivationTransitionResult(
            activationPrerequisites,
            transition,
            "Activation prerequisites were validated immediately before lifecycle transition."
        );
    }

    public static Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentCacheBatchAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        DocumentCacheAdministrativeClearBatchRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(request);

        return ClearBatchAsync(
            mutexSession.CreateCommandExecutor(),
            commands.ClearDocumentCacheBatchCommandText,
            DocumentCacheAdministrativeClearTarget.DocumentCache,
            request,
            cancellationToken
        );
    }

    public static Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentProjectionWorkBatchAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        DocumentCacheAdministrativeClearBatchRequest request,
        DocumentCacheAdministrativeWorkClearance clearance,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(clearance);

        return ClearBatchAsync(
            mutexSession.CreateCommandExecutor(),
            commands.ClearDocumentProjectionWorkBatchCommandText,
            DocumentCacheAdministrativeClearTarget.DocumentProjectionWork,
            request,
            cancellationToken
        );
    }

    public static async Task<DocumentCacheAdministrativeProjectedStateEmptinessResult> ReadProjectedStateEmptinessAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);

        return await mutexSession
            .CreateCommandExecutor()
            .ExecuteReaderAsync(
                new RelationalCommand(commands.ProjectedStateEmptinessCommandText),
                static async (reader, readerCancellationToken) =>
                {
                    if (!await reader.ReadAsync(readerCancellationToken).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            "DocumentCache projected-state emptiness observation did not return a row."
                        );
                    }

                    bool documentCacheEmpty = ReadRequiredBoolean(reader, DocumentCacheEmptyColumnName);
                    bool documentProjectionWorkEmpty = ReadRequiredBoolean(
                        reader,
                        DocumentProjectionWorkEmptyColumnName
                    );

                    return new DocumentCacheAdministrativeProjectedStateEmptinessResult(
                        documentCacheEmpty,
                        documentProjectionWorkEmpty,
                        documentCacheEmpty && documentProjectionWorkEmpty
                            ? "DocumentCache projected state is empty."
                            : "DocumentCache projected state still has cache or durable-work rows."
                    );
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public static async Task<DocumentCacheAdministrativeBaselineBoundaryResult> CaptureBaselineBoundaryAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);

        return await mutexSession
            .CreateCommandExecutor()
            .ExecuteReaderAsync(
                new RelationalCommand(commands.CaptureBaselineBoundaryCommandText),
                static async (reader, readerCancellationToken) =>
                {
                    if (!await reader.ReadAsync(readerCancellationToken).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            "DocumentCache baseline boundary observation did not return a row."
                        );
                    }

                    long? boundaryDocumentId = ReadOptionalInt64(reader, BoundaryDocumentIdColumnName);

                    return new DocumentCacheAdministrativeBaselineBoundaryResult(
                        boundaryDocumentId,
                        boundaryDocumentId is null
                            ? "DocumentCache baseline boundary found no canonical documents."
                            : "DocumentCache baseline boundary captured."
                    );
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public static async Task<DocumentCacheAdministrativeWorkHighWaterObservationResult> ObserveWorkHighWaterAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        DocumentCacheAdministrativeWorkHighWaterObservationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(request);

        ImmutableArray<long>.Builder diagnosticDocumentIds = ImmutableArray.CreateBuilder<long>(
            Math.Min(request.DiagnosticCapacity, request.HighWaterPlusOne)
        );
        var observedRows = 0;

        await mutexSession
            .CreateCommandExecutor()
            .ExecuteReaderAsync(
                new RelationalCommand(
                    commands.ObserveWorkHighWaterCommandText,
                    [new RelationalParameter("@highWaterPlusOne", request.HighWaterPlusOne)]
                ),
                async (reader, readerCancellationToken) =>
                {
                    while (await reader.ReadAsync(readerCancellationToken).ConfigureAwait(false))
                    {
                        observedRows++;
                        if (diagnosticDocumentIds.Count < request.DiagnosticCapacity)
                        {
                            diagnosticDocumentIds.Add(ReadRequiredInt64(reader, ClearedDocumentIdColumnName));
                        }
                    }

                    return true;
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        return new DocumentCacheAdministrativeWorkHighWaterObservationResult(
            request.HighWaterMark,
            observedRows,
            diagnosticDocumentIds.ToImmutable(),
            observedRows >= request.HighWaterMark
                ? "DocumentProjectionWork is at or above the baseline high-water mark."
                : "DocumentProjectionWork is below the baseline high-water mark."
        );
    }

    public static async Task<DocumentCacheAdministrativeBaselineSeedPageResult> SeedBaselinePageAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        DocumentCacheAdministrativeBaselineSeedPageRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(request);

        ImmutableArray<DocumentCacheAdministrativeBaselineSeededDocument>.Builder documents =
            ImmutableArray.CreateBuilder<DocumentCacheAdministrativeBaselineSeededDocument>(request.PageSize);

        await mutexSession
            .CreateCommandExecutor()
            .ExecuteReaderAsync(
                new RelationalCommand(
                    commands.SeedBaselinePageCommandText,
                    [
                        new RelationalParameter("@boundaryDocumentId", request.BoundaryDocumentId),
                        new RelationalParameter("@afterDocumentId", request.AfterDocumentId),
                        new RelationalParameter("@pageSize", request.PageSize),
                    ]
                ),
                async (reader, readerCancellationToken) =>
                {
                    while (await reader.ReadAsync(readerCancellationToken).ConfigureAwait(false))
                    {
                        documents.Add(
                            new DocumentCacheAdministrativeBaselineSeededDocument(
                                ReadRequiredInt64(reader, ClearedDocumentIdColumnName),
                                ReadRequiredInt64(reader, SourceContentVersionColumnName),
                                ReadOptionalInt64(reader, PreviousRequiredContentVersionColumnName),
                                ReadMutationKind(reader)
                            )
                        );
                    }

                    return true;
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        ImmutableArray<DocumentCacheAdministrativeBaselineSeededDocument> seededDocuments =
            documents.ToImmutable();
        DocumentCacheAdministrativeBaselineSeedPageStatus status = seededDocuments switch
        {
            { IsEmpty: true } => DocumentCacheAdministrativeBaselineSeedPageStatus.Empty,
            _ when seededDocuments.Any(document => document.RequiresRetry) =>
                DocumentCacheAdministrativeBaselineSeedPageStatus.RetryFromLastCommittedKey,
            _ => DocumentCacheAdministrativeBaselineSeedPageStatus.PageSeeded,
        };

        return new DocumentCacheAdministrativeBaselineSeedPageResult(
            status,
            request.BoundaryDocumentId,
            request.AfterDocumentId,
            request.PageSize,
            seededDocuments,
            status switch
            {
                DocumentCacheAdministrativeBaselineSeedPageStatus.Empty =>
                    "DocumentCache baseline seed page found no canonical rows.",
                DocumentCacheAdministrativeBaselineSeedPageStatus.RetryFromLastCommittedKey =>
                    "DocumentCache baseline seed page was invalidated by a concurrent change.",
                _ => "DocumentCache baseline seed page completed.",
            }
        );
    }

    public static async Task<DocumentCacheAdministrativeScrubPageResult> ScrubPageAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        DocumentCacheAdministrativeScrubPageRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(request);

        ImmutableArray<DocumentCacheAdministrativeScrubbedDocument>.Builder documents =
            ImmutableArray.CreateBuilder<DocumentCacheAdministrativeScrubbedDocument>(request.PageSize);

        await mutexSession
            .CreateCommandExecutor()
            .ExecuteReaderAsync(
                new RelationalCommand(
                    commands.ScrubPageCommandText,
                    [
                        new RelationalParameter("@boundaryDocumentId", request.BoundaryDocumentId),
                        new RelationalParameter("@afterDocumentId", request.AfterDocumentId),
                        new RelationalParameter("@pageSize", request.PageSize),
                    ]
                ),
                async (reader, readerCancellationToken) =>
                {
                    while (await reader.ReadAsync(readerCancellationToken).ConfigureAwait(false))
                    {
                        documents.Add(
                            new DocumentCacheAdministrativeScrubbedDocument(
                                ReadRequiredInt64(reader, ClearedDocumentIdColumnName),
                                ReadRequiredInt64(reader, SourceContentVersionColumnName),
                                ReadOptionalInt64(reader, CacheContentVersionColumnName),
                                ReadOptionalInt64(reader, PreviousRequiredContentVersionColumnName),
                                ReadScrubMutationKind(reader)
                            )
                        );
                    }

                    return true;
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        ImmutableArray<DocumentCacheAdministrativeScrubbedDocument> scrubbedDocuments =
            documents.ToImmutable();
        DocumentCacheAdministrativeScrubPageStatus status = scrubbedDocuments switch
        {
            { IsEmpty: true } => DocumentCacheAdministrativeScrubPageStatus.Empty,
            _ when scrubbedDocuments.Any(document => document.LatchSet) =>
                DocumentCacheAdministrativeScrubPageStatus.CacheAheadLatched,
            _ when scrubbedDocuments.Any(document => document.RequiresRetry) =>
                DocumentCacheAdministrativeScrubPageStatus.RetryFromLastCommittedKey,
            _ => DocumentCacheAdministrativeScrubPageStatus.PageScrubbed,
        };

        return new DocumentCacheAdministrativeScrubPageResult(
            status,
            request.BoundaryDocumentId,
            request.AfterDocumentId,
            request.PageSize,
            scrubbedDocuments,
            status switch
            {
                DocumentCacheAdministrativeScrubPageStatus.Empty =>
                    "DocumentCache explicit scrub page found no canonical rows.",
                DocumentCacheAdministrativeScrubPageStatus.CacheAheadLatched =>
                    "DocumentCache explicit scrub confirmed cache-ahead state and set the recovery latch.",
                DocumentCacheAdministrativeScrubPageStatus.RetryFromLastCommittedKey =>
                    "DocumentCache explicit scrub page was invalidated by a concurrent change.",
                _ => "DocumentCache explicit scrub page completed.",
            }
        );
    }

    private static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        IRelationalCommandExecutor executor,
        DocumentCacheAdministrativePrimitiveCommands commands,
        DocumentCacheAdministrativeStateLockMode lockMode,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await executor
                .ExecuteReaderAsync(
                    new RelationalCommand(commands.GetLifecycleObservationCommandText(lockMode)),
                    (reader, readerCancellationToken) =>
                        ReadLifecycleAsync(reader, commands.LifecycleReaderQuery, readerCancellationToken),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Unreadable,
                "dms.DocumentCacheState is unreadable."
            );
        }
    }

    private static async Task<DocumentCacheAdministrativeClearBatchResult> ClearBatchAsync(
        IRelationalCommandExecutor executor,
        string commandText,
        DocumentCacheAdministrativeClearTarget target,
        DocumentCacheAdministrativeClearBatchRequest request,
        CancellationToken cancellationToken
    )
    {
        ImmutableArray<long>.Builder clearedDocumentIds = ImmutableArray.CreateBuilder<long>(
            request.PageSize
        );

        await executor
            .ExecuteReaderAsync(
                new RelationalCommand(commandText, [new RelationalParameter("@pageSize", request.PageSize)]),
                async (reader, readerCancellationToken) =>
                {
                    while (await reader.ReadAsync(readerCancellationToken).ConfigureAwait(false))
                    {
                        clearedDocumentIds.Add(ReadRequiredInt64(reader, ClearedDocumentIdColumnName));
                    }

                    return true;
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        ImmutableArray<long> sortedDocumentIds = clearedDocumentIds.ToImmutable().Sort();

        return new DocumentCacheAdministrativeClearBatchResult(
            target,
            request.PageSize,
            sortedDocumentIds,
            sortedDocumentIds.IsEmpty
                ? "DocumentCache administrative clear batch found no rows."
                : "DocumentCache administrative clear batch deleted bounded rows."
        );
    }

    private static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        IRelationalCommandReader reader,
        DocumentCacheLifecycleReaderQuery query,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Missing,
                "dms.DocumentCacheState singleton row is missing."
            );
        }

        string? lifecycleText = ReadOptionalString(reader, query.LifecycleColumnName);
        bool? cacheAheadRecoveryRequired = ReadOptionalBoolean(
            reader,
            query.CacheAheadRecoveryRequiredColumnName
        );

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Invalid,
                "dms.DocumentCacheState must contain exactly one singleton row."
            );
        }

        if (
            lifecycleText is null
            || cacheAheadRecoveryRequired is null
            || !Enum.TryParse(lifecycleText, ignoreCase: false, out DocumentCacheLifecycleState lifecycle)
            || !Enum.IsDefined(lifecycle)
        )
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Invalid,
                "dms.DocumentCacheState lifecycle row is invalid."
            );
        }

        return DocumentCacheLifecycleReadResult.Success(
            new DocumentCacheLifecycleObservation(lifecycle, cacheAheadRecoveryRequired.Value)
        );
    }

    private static async Task ExecuteNoResultAsync(
        IRelationalCommandExecutor executor,
        RelationalCommand command,
        CancellationToken cancellationToken
    )
    {
        await executor
            .ExecuteReaderAsync(
                command,
                static (reader, readerCancellationToken) =>
                {
                    _ = reader;
                    _ = readerCancellationToken;
                    return Task.FromResult(true);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateSqlServerActivationPrerequisitesAsync(
        IRelationalCommandExecutor executor,
        string activationPrerequisiteCommandText,
        CancellationToken cancellationToken
    )
    {
        try
        {
            DocumentCacheSqlServerPrerequisiteDetails details = await executor
                .ExecuteReaderAsync(
                    new RelationalCommand(activationPrerequisiteCommandText),
                    static async (reader, readerCancellationToken) =>
                    {
                        if (!await reader.ReadAsync(readerCancellationToken).ConfigureAwait(false))
                        {
                            return UnreadableSqlServerPrerequisites();
                        }

                        return new DocumentCacheSqlServerPrerequisiteDetails(
                            ReadSqlServerPrerequisite(
                                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                                ReadOptionalInt(reader, ReadCommittedSnapshotColumnName),
                                "SQL Server READ_COMMITTED_SNAPSHOT is enabled.",
                                "SQL Server READ_COMMITTED_SNAPSHOT is disabled.",
                                "SQL Server READ_COMMITTED_SNAPSHOT is unreadable."
                            ),
                            ReadSqlServerPrerequisite(
                                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                                ReadOptionalInt(reader, NestedTriggersColumnName),
                                "SQL Server nested triggers are enabled.",
                                "SQL Server nested triggers are disabled.",
                                "SQL Server nested triggers are unreadable."
                            )
                        );
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);

            return DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(details);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
                UnreadableSqlServerPrerequisites()
            );
        }
    }

    private static DocumentCacheProviderPrerequisiteResult ReadSqlServerPrerequisite(
        DocumentCacheProviderPrerequisiteName name,
        int? value,
        string satisfiedMessage,
        string disabledMessage,
        string unreadableMessage
    ) =>
        value switch
        {
            1 => new DocumentCacheProviderPrerequisiteResult(
                name,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                satisfiedMessage
            ),
            0 => new DocumentCacheProviderPrerequisiteResult(
                name,
                DocumentCacheProviderPrerequisiteStatus.Disabled,
                disabledMessage
            ),
            _ => Unreadable(name, unreadableMessage),
        };

    private static DocumentCacheSqlServerPrerequisiteDetails UnreadableSqlServerPrerequisites() =>
        new(
            Unreadable(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                "SQL Server READ_COMMITTED_SNAPSHOT is unreadable."
            ),
            Unreadable(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                "SQL Server nested triggers are unreadable."
            )
        );

    private static DocumentCacheProviderPrerequisiteResult Unreadable(
        DocumentCacheProviderPrerequisiteName name,
        string message
    ) => new(name, DocumentCacheProviderPrerequisiteStatus.Unreadable, message);

    private static IReadOnlyList<RelationalParameter> CreateTransitionParameters(
        DocumentCacheAdministrativeLifecycleTransitionRequest request
    ) =>
        [
            new("@expectedLifecycle", request.ExpectedLifecycle.ToString()),
            new("@expectedCacheAheadRecoveryRequired", request.ExpectedCacheAheadRecoveryRequired),
            new("@nextLifecycle", request.NextLifecycle.ToString()),
            new("@nextCacheAheadRecoveryRequired", request.NextCacheAheadRecoveryRequired),
        ];

    private static DocumentCacheAdministrativeBaselineWorkMutationKind ReadMutationKind(
        IRelationalCommandReader reader
    )
    {
        string mutationKind =
            ReadOptionalString(reader, MutationKindColumnName)
            ?? throw new InvalidOperationException("Required baseline mutation kind was null.");

        return
            Enum.TryParse(
                mutationKind,
                ignoreCase: false,
                out DocumentCacheAdministrativeBaselineWorkMutationKind parsed
            ) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException($"Unsupported baseline mutation kind '{mutationKind}'.");
    }

    private static DocumentCacheAdministrativeScrubMutationKind ReadScrubMutationKind(
        IRelationalCommandReader reader
    )
    {
        string mutationKind =
            ReadOptionalString(reader, MutationKindColumnName)
            ?? throw new InvalidOperationException("Required scrub mutation kind was null.");

        return
            Enum.TryParse(
                mutationKind,
                ignoreCase: false,
                out DocumentCacheAdministrativeScrubMutationKind parsed
            ) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException($"Unsupported scrub mutation kind '{mutationKind}'.");
    }

    private static long ReadRequiredInt64(IRelationalCommandReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidOperationException($"Required bigint column '{columnName}' was null.");
        }

        object value = reader.GetFieldValue<object>(ordinal);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static long? ReadOptionalInt64(IRelationalCommandReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        object value = reader.GetFieldValue<object>(ordinal);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static string? ReadOptionalString(IRelationalCommandReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<string>(ordinal);
    }

    private static bool ReadRequiredBoolean(IRelationalCommandReader reader, string columnName) =>
        ReadOptionalBoolean(reader, columnName)
        ?? throw new InvalidOperationException($"Required boolean column '{columnName}' was null.");

    private static bool? ReadOptionalBoolean(IRelationalCommandReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        object value = reader.GetFieldValue<object>(ordinal);
        return value switch
        {
            bool booleanValue => booleanValue,
            byte byteValue => byteValue != 0,
            short shortValue => shortValue != 0,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
        };
    }

    private static int? ReadOptionalInt(IRelationalCommandReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        object value = reader.GetFieldValue<object>(ordinal);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static DocumentCacheAdministrativePrimitiveCommands CreateCommands(
        SqlDialect dialect,
        RelationalProviderToken providerToken
    )
    {
        string lifecycleColumn = DocumentCacheInventoryDefinition
            .DocumentCacheStateColumns
            .ProjectionLifecycleState
            .Value;
        string cacheAheadRecoveryRequiredColumn = DocumentCacheInventoryDefinition
            .DocumentCacheStateColumns
            .CacheAheadRecoveryRequired
            .Value;

        return new DocumentCacheAdministrativePrimitiveCommands(
            providerToken,
            RenderLifecycleObservationCommandText(dialect, exclusive: false),
            RenderLifecycleObservationCommandText(dialect, exclusive: true),
            RenderGuardedActivationDocumentLockCommandText(dialect),
            RenderGuardedActivationEmptyStateCommandText(dialect),
            RenderTransitionLifecycleCommandText(dialect),
            RenderClearBatchCommandText(dialect, DocumentCacheAdministrativeClearTarget.DocumentCache),
            RenderClearBatchCommandText(
                dialect,
                DocumentCacheAdministrativeClearTarget.DocumentProjectionWork
            ),
            RenderProjectedStateEmptinessCommandText(dialect),
            RenderCaptureBaselineBoundaryCommandText(dialect),
            RenderObserveWorkHighWaterCommandText(dialect),
            RenderSeedBaselinePageCommandText(dialect),
            RenderScrubPageCommandText(dialect),
            dialect == SqlDialect.Mssql ? RenderSqlServerActivationPrerequisiteCommandText() : null,
            new DocumentCacheLifecycleReaderQuery(
                ExistsCommandText: string.Empty,
                ReadLifecycleCommandText: string.Empty,
                lifecycleColumn,
                cacheAheadRecoveryRequiredColumn,
                providerToken
            )
        );
    }

    private static string RenderLifecycleObservationCommandText(SqlDialect dialect, bool exclusive)
    {
        string qualifiedTable = Quote(DocumentCacheInventoryDefinition.DocumentCacheState, dialect);
        string stateIdColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.StateId,
            dialect
        );
        string lifecycleColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.ProjectionLifecycleState,
            dialect
        );
        string cacheAheadRecoveryRequiredColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.CacheAheadRecoveryRequired,
            dialect
        );

        return dialect switch
        {
            SqlDialect.Pgsql => $"""
                SELECT {lifecycleColumn}, {cacheAheadRecoveryRequiredColumn}
                FROM {qualifiedTable}
                WHERE {stateIdColumn} = 1
                {(exclusive ? "FOR UPDATE" : "FOR SHARE")};
                """,
            SqlDialect.Mssql => $"""
                SELECT TOP (2) {lifecycleColumn}, {cacheAheadRecoveryRequiredColumn}
                FROM {qualifiedTable} WITH ({(exclusive ? "XLOCK, " : string.Empty)}HOLDLOCK)
                WHERE {stateIdColumn} = 1;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderGuardedActivationDocumentLockCommandText(SqlDialect dialect)
    {
        string documentTable = Quote(DocumentCacheInventoryDefinition.Document, dialect);
        string documentIdColumn = Quote(DocumentCacheInventoryDefinition.DocumentColumns.DocumentId, dialect);

        return dialect switch
        {
            SqlDialect.Pgsql => $"LOCK TABLE {documentTable} IN SHARE MODE;",
            SqlDialect.Mssql => $"""
                SELECT TOP (1) {documentIdColumn}
                FROM {documentTable} WITH (TABLOCK, HOLDLOCK);
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderGuardedActivationEmptyStateCommandText(SqlDialect dialect)
    {
        string documentTable = Quote(DocumentCacheInventoryDefinition.Document, dialect);
        string cacheTable = Quote(DocumentCacheInventoryDefinition.DocumentCache, dialect);
        string workTable = Quote(DocumentCacheInventoryDefinition.DocumentProjectionWork, dialect);

        return dialect switch
        {
            SqlDialect.Pgsql => $"""
                SELECT
                    NOT EXISTS (SELECT 1 FROM {documentTable} LIMIT 1) AS "{CanonicalDocumentsEmptyColumnName}",
                    NOT EXISTS (SELECT 1 FROM {cacheTable} LIMIT 1) AS "{DocumentCacheEmptyColumnName}",
                    NOT EXISTS (SELECT 1 FROM {workTable} LIMIT 1) AS "{DocumentProjectionWorkEmptyColumnName}";
                """,
            SqlDialect.Mssql => $"""
                SELECT
                    CAST(CASE WHEN NOT EXISTS (SELECT TOP (1) 1 FROM {documentTable}) THEN 1 ELSE 0 END AS bit) AS [{CanonicalDocumentsEmptyColumnName}],
                    CAST(CASE WHEN NOT EXISTS (SELECT TOP (1) 1 FROM {cacheTable}) THEN 1 ELSE 0 END AS bit) AS [{DocumentCacheEmptyColumnName}],
                    CAST(CASE WHEN NOT EXISTS (SELECT TOP (1) 1 FROM {workTable}) THEN 1 ELSE 0 END AS bit) AS [{DocumentProjectionWorkEmptyColumnName}];
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderTransitionLifecycleCommandText(SqlDialect dialect)
    {
        string stateTable = Quote(DocumentCacheInventoryDefinition.DocumentCacheState, dialect);
        string stateIdColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.StateId,
            dialect
        );
        string lifecycleColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.ProjectionLifecycleState,
            dialect
        );
        string cacheAheadRecoveryRequiredColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.CacheAheadRecoveryRequired,
            dialect
        );

        return dialect switch
        {
            SqlDialect.Pgsql => $"""
                UPDATE {stateTable}
                SET {lifecycleColumn} = @nextLifecycle,
                    {cacheAheadRecoveryRequiredColumn} = @nextCacheAheadRecoveryRequired
                WHERE {stateIdColumn} = 1
                  AND {lifecycleColumn} = @expectedLifecycle
                  AND {cacheAheadRecoveryRequiredColumn} = @expectedCacheAheadRecoveryRequired
                RETURNING {lifecycleColumn}, {cacheAheadRecoveryRequiredColumn};
                """,
            SqlDialect.Mssql => $"""
                DECLARE @transitioned table (
                    {lifecycleColumn} varchar(16) NOT NULL,
                    {cacheAheadRecoveryRequiredColumn} bit NOT NULL
                );

                UPDATE {stateTable} WITH (XLOCK, HOLDLOCK)
                SET {lifecycleColumn} = @nextLifecycle,
                    {cacheAheadRecoveryRequiredColumn} = @nextCacheAheadRecoveryRequired
                OUTPUT inserted.{lifecycleColumn}, inserted.{cacheAheadRecoveryRequiredColumn}
                INTO @transitioned
                WHERE {stateIdColumn} = 1
                  AND {lifecycleColumn} = @expectedLifecycle
                  AND {cacheAheadRecoveryRequiredColumn} = @expectedCacheAheadRecoveryRequired;

                SELECT TOP (2) {lifecycleColumn}, {cacheAheadRecoveryRequiredColumn}
                FROM @transitioned;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderClearBatchCommandText(
        SqlDialect dialect,
        DocumentCacheAdministrativeClearTarget target
    )
    {
        DbTableName table = target switch
        {
            DocumentCacheAdministrativeClearTarget.DocumentCache =>
                DocumentCacheInventoryDefinition.DocumentCache,
            DocumentCacheAdministrativeClearTarget.DocumentProjectionWork =>
                DocumentCacheInventoryDefinition.DocumentProjectionWork,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported clear target."),
        };

        string qualifiedTable = Quote(table, dialect);
        string documentIdColumn = Quote(DocumentCacheInventoryDefinition.DocumentColumns.DocumentId, dialect);
        string resultColumn = Quote(new DbColumnName(ClearedDocumentIdColumnName), dialect);

        return dialect switch
        {
            SqlDialect.Pgsql => $"""
                WITH bounded_rows AS (
                    SELECT {documentIdColumn}
                    FROM {qualifiedTable}
                    ORDER BY {documentIdColumn}
                    LIMIT @pageSize
                )
                DELETE FROM {qualifiedTable} AS target
                USING bounded_rows
                WHERE target.{documentIdColumn} = bounded_rows.{documentIdColumn}
                RETURNING target.{documentIdColumn} AS {resultColumn};
                """,
            SqlDialect.Mssql => $"""
                WITH bounded_rows AS (
                    SELECT TOP (@pageSize) {documentIdColumn}
                    FROM {qualifiedTable}
                    ORDER BY {documentIdColumn}
                )
                DELETE target
                OUTPUT deleted.{documentIdColumn}
                FROM {qualifiedTable} AS target
                INNER JOIN bounded_rows ON bounded_rows.{documentIdColumn} = target.{documentIdColumn};
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderProjectedStateEmptinessCommandText(SqlDialect dialect)
    {
        string cacheTable = Quote(DocumentCacheInventoryDefinition.DocumentCache, dialect);
        string workTable = Quote(DocumentCacheInventoryDefinition.DocumentProjectionWork, dialect);

        return dialect switch
        {
            SqlDialect.Pgsql => $"""
                SELECT
                    NOT EXISTS (SELECT 1 FROM {cacheTable} LIMIT 1) AS "{DocumentCacheEmptyColumnName}",
                    NOT EXISTS (SELECT 1 FROM {workTable} LIMIT 1) AS "{DocumentProjectionWorkEmptyColumnName}";
                """,
            SqlDialect.Mssql => $"""
                SELECT
                    CAST(CASE WHEN NOT EXISTS (SELECT TOP (1) 1 FROM {cacheTable}) THEN 1 ELSE 0 END AS bit) AS [{DocumentCacheEmptyColumnName}],
                    CAST(CASE WHEN NOT EXISTS (SELECT TOP (1) 1 FROM {workTable}) THEN 1 ELSE 0 END AS bit) AS [{DocumentProjectionWorkEmptyColumnName}];
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderCaptureBaselineBoundaryCommandText(SqlDialect dialect)
    {
        string documentTable = Quote(DocumentCacheInventoryDefinition.Document, dialect);
        string documentIdColumn = Quote(DocumentCacheInventoryDefinition.DocumentColumns.DocumentId, dialect);
        string boundaryColumn = Quote(new DbColumnName(BoundaryDocumentIdColumnName), dialect);

        return dialect switch
        {
            SqlDialect.Pgsql => $"""
                SELECT MAX({documentIdColumn}) AS {boundaryColumn}
                FROM {documentTable};
                """,
            SqlDialect.Mssql => $"""
                SELECT MAX({documentIdColumn}) AS {boundaryColumn}
                FROM {documentTable};
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderObserveWorkHighWaterCommandText(SqlDialect dialect)
    {
        string workTable = Quote(DocumentCacheInventoryDefinition.DocumentProjectionWork, dialect);
        string documentIdColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.DocumentId,
            dialect
        );
        string firstEnqueuedAtColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.FirstEnqueuedAt,
            dialect
        );

        return dialect switch
        {
            SqlDialect.Pgsql => $"""
                SELECT {documentIdColumn}
                FROM {workTable}
                ORDER BY {firstEnqueuedAtColumn}, {documentIdColumn}
                LIMIT @highWaterPlusOne;
                """,
            SqlDialect.Mssql => $"""
                SELECT TOP (@highWaterPlusOne) {documentIdColumn}
                FROM {workTable}
                ORDER BY {firstEnqueuedAtColumn}, {documentIdColumn};
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderSeedBaselinePageCommandText(SqlDialect dialect)
    {
        string documentTable = Quote(DocumentCacheInventoryDefinition.Document, dialect);
        string workTable = Quote(DocumentCacheInventoryDefinition.DocumentProjectionWork, dialect);
        string documentIdColumn = Quote(DocumentCacheInventoryDefinition.DocumentColumns.DocumentId, dialect);
        string contentVersionColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentColumns.ContentVersion,
            dialect
        );
        string workRequiredContentVersionColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.RequiredContentVersion,
            dialect
        );
        string firstEnqueuedAtColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.FirstEnqueuedAt,
            dialect
        );
        string lastEnqueuedAtColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.LastEnqueuedAt,
            dialect
        );
        string resultDocumentIdColumn = Quote(new DbColumnName(ClearedDocumentIdColumnName), dialect);
        string sourceContentVersionColumn = Quote(new DbColumnName(SourceContentVersionColumnName), dialect);
        string previousRequiredContentVersionColumn = Quote(
            new DbColumnName(PreviousRequiredContentVersionColumnName),
            dialect
        );
        string mutationKindColumn = Quote(new DbColumnName(MutationKindColumnName), dialect);

        return dialect switch
        {
            SqlDialect.Pgsql => $"""
                WITH bounded_source AS (
                    SELECT source.{documentIdColumn}, source.{contentVersionColumn}
                    FROM {documentTable} AS source
                    WHERE source.{documentIdColumn} > @afterDocumentId
                      AND source.{documentIdColumn} <= @boundaryDocumentId
                    ORDER BY source.{documentIdColumn}
                    LIMIT @pageSize
                    FOR SHARE
                ),
                observed AS (
                    SELECT
                        bounded_source.{documentIdColumn},
                        bounded_source.{contentVersionColumn},
                        work.{workRequiredContentVersionColumn} AS {previousRequiredContentVersionColumn}
                    FROM bounded_source
                    LEFT JOIN {workTable} AS work
                      ON work.{documentIdColumn} = bounded_source.{documentIdColumn}
                ),
                upserted AS (
                    INSERT INTO {workTable} AS work (
                        {documentIdColumn},
                        {workRequiredContentVersionColumn},
                        {firstEnqueuedAtColumn},
                        {lastEnqueuedAtColumn}
                    )
                    SELECT
                        observed.{documentIdColumn},
                        observed.{contentVersionColumn},
                        CURRENT_TIMESTAMP,
                        CURRENT_TIMESTAMP
                    FROM observed
                    ON CONFLICT ({documentIdColumn}) DO UPDATE
                    SET {workRequiredContentVersionColumn} = EXCLUDED.{workRequiredContentVersionColumn},
                        {lastEnqueuedAtColumn} = CASE
                            WHEN work.{workRequiredContentVersionColumn} < EXCLUDED.{workRequiredContentVersionColumn}
                                THEN EXCLUDED.{lastEnqueuedAtColumn}
                            ELSE work.{lastEnqueuedAtColumn}
                        END
                    WHERE work.{workRequiredContentVersionColumn} <> EXCLUDED.{workRequiredContentVersionColumn}
                    RETURNING work.{documentIdColumn}
                )
                SELECT
                    observed.{documentIdColumn} AS {resultDocumentIdColumn},
                    observed.{contentVersionColumn} AS {sourceContentVersionColumn},
                    observed.{previousRequiredContentVersionColumn},
                    CASE
                        WHEN upserted.{documentIdColumn} IS NULL THEN 'None'
                        WHEN observed.{previousRequiredContentVersionColumn} IS NULL THEN 'Inserted'
                        WHEN observed.{previousRequiredContentVersionColumn} < observed.{contentVersionColumn} THEN 'Advanced'
                        WHEN observed.{previousRequiredContentVersionColumn} > observed.{contentVersionColumn} THEN 'Lowered'
                        ELSE 'None'
                    END AS {mutationKindColumn}
                FROM observed
                LEFT JOIN upserted
                  ON upserted.{documentIdColumn} = observed.{documentIdColumn}
                ORDER BY observed.{documentIdColumn};
                """,
            SqlDialect.Mssql => $"""
                DECLARE @observed table (
                    [DocumentId] bigint NOT NULL PRIMARY KEY,
                    [SourceContentVersion] bigint NOT NULL,
                    [PreviousRequiredContentVersion] bigint NULL
                );
                DECLARE @mutated table (
                    [DocumentId] bigint NOT NULL PRIMARY KEY
                );
                DECLARE @now datetimeoffset = SYSUTCDATETIME();

                INSERT INTO @observed (
                    [DocumentId],
                    [SourceContentVersion],
                    [PreviousRequiredContentVersion]
                )
                SELECT TOP (@pageSize)
                    source.{documentIdColumn},
                    source.{contentVersionColumn},
                    work.{workRequiredContentVersionColumn}
                FROM {documentTable} AS source WITH (HOLDLOCK)
                LEFT JOIN {workTable} AS work WITH (UPDLOCK, HOLDLOCK)
                  ON work.{documentIdColumn} = source.{documentIdColumn}
                WHERE source.{documentIdColumn} > @afterDocumentId
                  AND source.{documentIdColumn} <= @boundaryDocumentId
                ORDER BY source.{documentIdColumn};

                UPDATE work
                SET {workRequiredContentVersionColumn} = observed.[SourceContentVersion],
                    {lastEnqueuedAtColumn} = CASE
                        WHEN work.{workRequiredContentVersionColumn} < observed.[SourceContentVersion]
                            THEN @now
                        ELSE work.{lastEnqueuedAtColumn}
                    END
                OUTPUT inserted.{documentIdColumn} INTO @mutated ([DocumentId])
                FROM {workTable} AS work
                INNER JOIN @observed AS observed
                  ON observed.[DocumentId] = work.{documentIdColumn}
                INNER JOIN {documentTable} AS source WITH (HOLDLOCK)
                  ON source.{documentIdColumn} = observed.[DocumentId]
                 AND source.{contentVersionColumn} = observed.[SourceContentVersion]
                WHERE observed.[PreviousRequiredContentVersion] IS NOT NULL
                  AND work.{workRequiredContentVersionColumn} = observed.[PreviousRequiredContentVersion]
                  AND work.{workRequiredContentVersionColumn} <> observed.[SourceContentVersion];

                INSERT INTO {workTable} (
                    {documentIdColumn},
                    {workRequiredContentVersionColumn},
                    {firstEnqueuedAtColumn},
                    {lastEnqueuedAtColumn}
                )
                OUTPUT inserted.{documentIdColumn} INTO @mutated ([DocumentId])
                SELECT
                    observed.[DocumentId],
                    observed.[SourceContentVersion],
                    @now,
                    @now
                FROM @observed AS observed
                INNER JOIN {documentTable} AS source WITH (HOLDLOCK)
                  ON source.{documentIdColumn} = observed.[DocumentId]
                 AND source.{contentVersionColumn} = observed.[SourceContentVersion]
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM {workTable} AS work WITH (UPDLOCK, HOLDLOCK)
                    WHERE work.{documentIdColumn} = observed.[DocumentId]
                );

                SELECT
                    observed.[DocumentId] AS {resultDocumentIdColumn},
                    observed.[SourceContentVersion] AS {sourceContentVersionColumn},
                    observed.[PreviousRequiredContentVersion] AS {previousRequiredContentVersionColumn},
                    CASE
                        WHEN current_source.{documentIdColumn} IS NULL THEN 'Retry'
                        WHEN current_work.{workRequiredContentVersionColumn} = observed.[SourceContentVersion] THEN
                            CASE
                                WHEN mutated.[DocumentId] IS NULL THEN 'None'
                                WHEN observed.[PreviousRequiredContentVersion] IS NULL THEN 'Inserted'
                                WHEN observed.[PreviousRequiredContentVersion] < observed.[SourceContentVersion] THEN 'Advanced'
                                WHEN observed.[PreviousRequiredContentVersion] > observed.[SourceContentVersion] THEN 'Lowered'
                                ELSE 'None'
                            END
                        ELSE 'Retry'
                    END AS {mutationKindColumn}
                FROM @observed AS observed
                LEFT JOIN @mutated AS mutated
                  ON mutated.[DocumentId] = observed.[DocumentId]
                LEFT JOIN {documentTable} AS current_source WITH (HOLDLOCK)
                  ON current_source.{documentIdColumn} = observed.[DocumentId]
                 AND current_source.{contentVersionColumn} = observed.[SourceContentVersion]
                LEFT JOIN {workTable} AS current_work WITH (HOLDLOCK)
                  ON current_work.{documentIdColumn} = observed.[DocumentId]
                ORDER BY observed.[DocumentId];
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderScrubPageCommandText(SqlDialect dialect)
    {
        string documentTable = Quote(DocumentCacheInventoryDefinition.Document, dialect);
        string cacheTable = Quote(DocumentCacheInventoryDefinition.DocumentCache, dialect);
        string stateTable = Quote(DocumentCacheInventoryDefinition.DocumentCacheState, dialect);
        string workTable = Quote(DocumentCacheInventoryDefinition.DocumentProjectionWork, dialect);
        string documentIdColumn = Quote(DocumentCacheInventoryDefinition.DocumentColumns.DocumentId, dialect);
        string sourceContentVersionPhysicalColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentColumns.ContentVersion,
            dialect
        );
        string cacheContentVersionPhysicalColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheColumns.ContentVersion,
            dialect
        );
        string stateIdColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.StateId,
            dialect
        );
        string lifecycleColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.ProjectionLifecycleState,
            dialect
        );
        string cacheAheadRecoveryRequiredColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.CacheAheadRecoveryRequired,
            dialect
        );
        string workRequiredContentVersionColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.RequiredContentVersion,
            dialect
        );
        string firstEnqueuedAtColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.FirstEnqueuedAt,
            dialect
        );
        string lastEnqueuedAtColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.LastEnqueuedAt,
            dialect
        );
        string resultDocumentIdColumn = Quote(new DbColumnName(ClearedDocumentIdColumnName), dialect);
        string sourceContentVersionColumn = Quote(new DbColumnName(SourceContentVersionColumnName), dialect);
        string cacheContentVersionColumn = Quote(new DbColumnName(CacheContentVersionColumnName), dialect);
        string previousRequiredContentVersionColumn = Quote(
            new DbColumnName(PreviousRequiredContentVersionColumnName),
            dialect
        );
        string mutationKindColumn = Quote(new DbColumnName(MutationKindColumnName), dialect);

        return dialect switch
        {
            SqlDialect.Pgsql => $"""
                WITH bounded_source AS (
                    SELECT source.{documentIdColumn}, source.{sourceContentVersionPhysicalColumn}
                    FROM {documentTable} AS source
                    WHERE source.{documentIdColumn} > @afterDocumentId
                      AND source.{documentIdColumn} <= @boundaryDocumentId
                    ORDER BY source.{documentIdColumn}
                    LIMIT @pageSize
                    FOR SHARE
                ),
                observed AS (
                    SELECT
                        bounded_source.{documentIdColumn},
                        bounded_source.{sourceContentVersionPhysicalColumn} AS {sourceContentVersionColumn},
                        cache.{cacheContentVersionPhysicalColumn} AS {cacheContentVersionColumn},
                        work.{workRequiredContentVersionColumn} AS {previousRequiredContentVersionColumn}
                    FROM bounded_source
                    LEFT JOIN {cacheTable} AS cache
                      ON cache.{documentIdColumn} = bounded_source.{documentIdColumn}
                    LEFT JOIN {workTable} AS work
                      ON work.{documentIdColumn} = bounded_source.{documentIdColumn}
                ),
                cache_ahead AS (
                    SELECT observed.{documentIdColumn}
                    FROM observed
                    WHERE observed.{cacheContentVersionColumn} > observed.{sourceContentVersionColumn}
                    ORDER BY observed.{documentIdColumn}
                ),
                latched AS (
                    UPDATE {stateTable}
                    SET {cacheAheadRecoveryRequiredColumn} = TRUE
                    WHERE {stateIdColumn} = 1
                      AND {lifecycleColumn} = 'Tracking'
                      AND {cacheAheadRecoveryRequiredColumn} = FALSE
                      AND EXISTS (SELECT 1 FROM cache_ahead)
                    RETURNING {stateIdColumn}
                ),
                upserted AS (
                    INSERT INTO {workTable} AS work (
                        {documentIdColumn},
                        {workRequiredContentVersionColumn},
                        {firstEnqueuedAtColumn},
                        {lastEnqueuedAtColumn}
                    )
                    SELECT
                        observed.{documentIdColumn},
                        observed.{sourceContentVersionColumn},
                        CURRENT_TIMESTAMP,
                        CURRENT_TIMESTAMP
                    FROM observed
                    WHERE NOT EXISTS (SELECT 1 FROM cache_ahead)
                    ON CONFLICT ({documentIdColumn}) DO UPDATE
                    SET {workRequiredContentVersionColumn} = EXCLUDED.{workRequiredContentVersionColumn},
                        {lastEnqueuedAtColumn} = CASE
                            WHEN work.{workRequiredContentVersionColumn} < EXCLUDED.{workRequiredContentVersionColumn}
                                THEN EXCLUDED.{lastEnqueuedAtColumn}
                            ELSE work.{lastEnqueuedAtColumn}
                        END
                    WHERE (
                        SELECT candidate.{previousRequiredContentVersionColumn} IS NOT NULL
                          AND work.{workRequiredContentVersionColumn} = candidate.{previousRequiredContentVersionColumn}
                          AND work.{workRequiredContentVersionColumn} <> EXCLUDED.{workRequiredContentVersionColumn}
                        FROM observed AS candidate
                        WHERE candidate.{documentIdColumn} = work.{documentIdColumn}
                    )
                    RETURNING work.{documentIdColumn}
                )
                SELECT
                    observed.{documentIdColumn} AS {resultDocumentIdColumn},
                    observed.{sourceContentVersionColumn},
                    observed.{cacheContentVersionColumn},
                    observed.{previousRequiredContentVersionColumn},
                    CASE
                        WHEN cache_ahead.{documentIdColumn} IS NOT NULL AND EXISTS (SELECT 1 FROM latched) THEN 'CacheAheadLatchSet'
                        WHEN cache_ahead.{documentIdColumn} IS NOT NULL THEN 'Retry'
                        WHEN EXISTS (SELECT 1 FROM cache_ahead) THEN 'None'
                        WHEN upserted.{documentIdColumn} IS NULL
                             AND observed.{previousRequiredContentVersionColumn} IS DISTINCT FROM observed.{sourceContentVersionColumn} THEN 'Retry'
                        WHEN upserted.{documentIdColumn} IS NULL THEN 'None'
                        WHEN observed.{previousRequiredContentVersionColumn} IS NULL THEN 'Inserted'
                        WHEN observed.{previousRequiredContentVersionColumn} < observed.{sourceContentVersionColumn} THEN 'Advanced'
                        WHEN observed.{previousRequiredContentVersionColumn} > observed.{sourceContentVersionColumn} THEN 'Lowered'
                        ELSE 'None'
                    END AS {mutationKindColumn}
                FROM observed
                LEFT JOIN cache_ahead
                  ON cache_ahead.{documentIdColumn} = observed.{documentIdColumn}
                LEFT JOIN upserted
                  ON upserted.{documentIdColumn} = observed.{documentIdColumn}
                ORDER BY observed.{documentIdColumn};
                """,
            SqlDialect.Mssql => $"""
                DECLARE @observed table (
                    [DocumentId] bigint NOT NULL PRIMARY KEY,
                    [SourceContentVersion] bigint NOT NULL,
                    [CacheContentVersion] bigint NULL,
                    [PreviousRequiredContentVersion] bigint NULL
                );
                DECLARE @cacheAhead table (
                    [DocumentId] bigint NOT NULL PRIMARY KEY
                );
                DECLARE @mutated table (
                    [DocumentId] bigint NOT NULL PRIMARY KEY
                );
                DECLARE @now datetimeoffset = SYSUTCDATETIME();
                DECLARE @cacheAheadObserved bit = 0;
                DECLARE @latchSet bit = 0;

                INSERT INTO @observed (
                    [DocumentId],
                    [SourceContentVersion],
                    [CacheContentVersion],
                    [PreviousRequiredContentVersion]
                )
                SELECT TOP (@pageSize)
                    source.{documentIdColumn},
                    source.{sourceContentVersionPhysicalColumn},
                    cache.{cacheContentVersionPhysicalColumn},
                    work.{workRequiredContentVersionColumn}
                FROM {documentTable} AS source WITH (HOLDLOCK)
                LEFT JOIN {cacheTable} AS cache WITH (HOLDLOCK)
                  ON cache.{documentIdColumn} = source.{documentIdColumn}
                LEFT JOIN {workTable} AS work WITH (UPDLOCK, HOLDLOCK)
                  ON work.{documentIdColumn} = source.{documentIdColumn}
                WHERE source.{documentIdColumn} > @afterDocumentId
                  AND source.{documentIdColumn} <= @boundaryDocumentId
                ORDER BY source.{documentIdColumn};

                INSERT INTO @cacheAhead ([DocumentId])
                SELECT observed.[DocumentId]
                FROM @observed AS observed
                WHERE observed.[CacheContentVersion] > observed.[SourceContentVersion];

                IF EXISTS (SELECT 1 FROM @cacheAhead)
                BEGIN
                    SET @cacheAheadObserved = 1;

                    UPDATE {stateTable} WITH (XLOCK, HOLDLOCK)
                    SET {cacheAheadRecoveryRequiredColumn} = 1
                    WHERE {stateIdColumn} = 1
                      AND {lifecycleColumn} = 'Tracking'
                      AND {cacheAheadRecoveryRequiredColumn} = 0;

                    IF @@ROWCOUNT = 1
                    BEGIN
                        SET @latchSet = 1;
                    END
                END

                IF @cacheAheadObserved = 0
                BEGIN
                    UPDATE work
                    SET {workRequiredContentVersionColumn} = observed.[SourceContentVersion],
                        {lastEnqueuedAtColumn} = CASE
                            WHEN work.{workRequiredContentVersionColumn} < observed.[SourceContentVersion]
                                THEN @now
                            ELSE work.{lastEnqueuedAtColumn}
                        END
                    OUTPUT inserted.{documentIdColumn} INTO @mutated ([DocumentId])
                    FROM {workTable} AS work
                    INNER JOIN @observed AS observed
                      ON observed.[DocumentId] = work.{documentIdColumn}
                    INNER JOIN {documentTable} AS source WITH (HOLDLOCK)
                      ON source.{documentIdColumn} = observed.[DocumentId]
                     AND source.{sourceContentVersionPhysicalColumn} = observed.[SourceContentVersion]
                    WHERE observed.[PreviousRequiredContentVersion] IS NOT NULL
                      AND work.{workRequiredContentVersionColumn} = observed.[PreviousRequiredContentVersion]
                      AND work.{workRequiredContentVersionColumn} <> observed.[SourceContentVersion];

                    INSERT INTO {workTable} (
                        {documentIdColumn},
                        {workRequiredContentVersionColumn},
                        {firstEnqueuedAtColumn},
                        {lastEnqueuedAtColumn}
                    )
                    OUTPUT inserted.{documentIdColumn} INTO @mutated ([DocumentId])
                    SELECT
                        observed.[DocumentId],
                        observed.[SourceContentVersion],
                        @now,
                        @now
                    FROM @observed AS observed
                    INNER JOIN {documentTable} AS source WITH (HOLDLOCK)
                      ON source.{documentIdColumn} = observed.[DocumentId]
                     AND source.{sourceContentVersionPhysicalColumn} = observed.[SourceContentVersion]
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM {workTable} AS work WITH (UPDLOCK, HOLDLOCK)
                        WHERE work.{documentIdColumn} = observed.[DocumentId]
                    );
                END

                SELECT
                    observed.[DocumentId] AS {resultDocumentIdColumn},
                    observed.[SourceContentVersion] AS {sourceContentVersionColumn},
                    observed.[CacheContentVersion] AS {cacheContentVersionColumn},
                    observed.[PreviousRequiredContentVersion] AS {previousRequiredContentVersionColumn},
                    CASE
                        WHEN cache_ahead.[DocumentId] IS NOT NULL AND @latchSet = 1 THEN 'CacheAheadLatchSet'
                        WHEN cache_ahead.[DocumentId] IS NOT NULL THEN 'Retry'
                        WHEN @cacheAheadObserved = 1 THEN 'None'
                        WHEN current_source.{documentIdColumn} IS NULL THEN 'Retry'
                        WHEN mutated.[DocumentId] IS NOT NULL THEN
                            CASE
                                WHEN observed.[PreviousRequiredContentVersion] IS NULL THEN 'Inserted'
                                WHEN observed.[PreviousRequiredContentVersion] < observed.[SourceContentVersion] THEN 'Advanced'
                                WHEN observed.[PreviousRequiredContentVersion] > observed.[SourceContentVersion] THEN 'Lowered'
                                ELSE 'None'
                            END
                        WHEN observed.[PreviousRequiredContentVersion] = observed.[SourceContentVersion]
                             AND current_work.{workRequiredContentVersionColumn} = observed.[SourceContentVersion] THEN 'None'
                        ELSE 'Retry'
                    END AS {mutationKindColumn}
                FROM @observed AS observed
                LEFT JOIN @cacheAhead AS cache_ahead
                  ON cache_ahead.[DocumentId] = observed.[DocumentId]
                LEFT JOIN @mutated AS mutated
                  ON mutated.[DocumentId] = observed.[DocumentId]
                LEFT JOIN {documentTable} AS current_source WITH (HOLDLOCK)
                  ON current_source.{documentIdColumn} = observed.[DocumentId]
                 AND current_source.{sourceContentVersionPhysicalColumn} = observed.[SourceContentVersion]
                LEFT JOIN {workTable} AS current_work WITH (HOLDLOCK)
                  ON current_work.{documentIdColumn} = observed.[DocumentId]
                ORDER BY observed.[DocumentId];
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderSqlServerActivationPrerequisiteCommandText() =>
        $"""
            SELECT
                (
                    SELECT CONVERT(int, [is_read_committed_snapshot_on])
                    FROM [sys].[databases]
                    WHERE [name] = DB_NAME()
                ) AS [{ReadCommittedSnapshotColumnName}],
                (
                    SELECT CONVERT(int, [value_in_use])
                    FROM [sys].[configurations]
                    WHERE [name] = N'nested triggers'
                ) AS [{NestedTriggersColumnName}];
            """;

    private static string Quote(DbTableName tableName, SqlDialect dialect) =>
        SqlIdentifierQuoter.QuoteTableName(dialect, tableName);

    private static string Quote(DbColumnName columnName, SqlDialect dialect) =>
        SqlIdentifierQuoter.QuoteIdentifier(dialect, columnName);
}

file static class DocumentCacheAdministrativePrimitiveText
{
    private const int MaximumLength = 512;

    public static string Sanitize(string? message)
    {
        string sanitized = LoggingSanitizer.SanitizeForLogging(message);
        return sanitized.Length <= MaximumLength ? sanitized : sanitized[..MaximumLength];
    }
}
