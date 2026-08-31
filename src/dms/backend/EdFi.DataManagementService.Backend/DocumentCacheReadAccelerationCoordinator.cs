// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

public enum DocumentCacheReadAccelerationResourceKind
{
    Resource,
    Descriptor,
}

internal enum DocumentCacheReadAccelerationFallbackReason
{
    None,
    ReadAccelerationDisabled,
    InvalidTargetKey,
    SelectedDataStoreUnavailable,
    EffectiveTargetNotSelected,
    UnresolvedTarget,
    DerivativeTargetSelected,
    CacheLookupMiss,
    CacheLookupStale,
    CacheLookupSourceDrift,
    CacheLookupFenced,
    CacheLookupUnavailable,
    CacheLookupInvariantFailure,
}

public sealed record DocumentCacheReadAccelerationCandidate(
    long DocumentId,
    DocumentUuid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion,
    DateTimeOffset ContentLastModifiedAt
);

/// <param name="HighestSelectedAnchor">
/// The maximum continuation-anchor value the page selection that produced these candidates chose, in
/// whichever key that selection was ordered by, or <see langword="null"/> when it selected none.
/// Carried on the candidate page because a page served from cache is shaped from the candidates alone,
/// and a boundary left behind there would make the continuation header depend on cache state.
/// </param>
public sealed record DocumentCacheReadAccelerationCandidatePage(
    IReadOnlyList<DocumentCacheReadAccelerationCandidate> Candidates,
    long? TotalCount,
    long? HighestSelectedAnchor,
    bool IncludesTotalCount
)
{
    public bool IsEmpty => Candidates.Count == 0;
}

public abstract record DocumentCacheReadAccelerationGetByIdSelectionResult
{
    private DocumentCacheReadAccelerationGetByIdSelectionResult() { }

    public sealed record Complete(GetResult Result) : DocumentCacheReadAccelerationGetByIdSelectionResult;

    public sealed record Candidate(
        DocumentCacheReadAccelerationCandidate AuthorizedCandidate,
        Func<CancellationToken, Task<GetResult>> RelationalFallback
    ) : DocumentCacheReadAccelerationGetByIdSelectionResult;
}

public abstract record DocumentCacheReadAccelerationQuerySelectionResult
{
    private DocumentCacheReadAccelerationQuerySelectionResult() { }

    public sealed record Complete(QueryResult Result) : DocumentCacheReadAccelerationQuerySelectionResult;

    public sealed record CandidatePage(
        DocumentCacheReadAccelerationCandidatePage AuthorizedCandidatePage,
        Func<CancellationToken, Task<QueryResult>> RelationalFallback
    ) : DocumentCacheReadAccelerationQuerySelectionResult;
}

public sealed record DocumentCacheReadAccelerationGetByIdRequest(
    string TenantKey,
    MappingSet MappingSet,
    QualifiedResourceName Resource,
    DocumentUuid DocumentUuid,
    DocumentCacheReadAccelerationResourceKind ResourceKind,
    Func<CancellationToken, Task<GetResult>> RelationalFallback,
    Func<
        CancellationToken,
        Task<DocumentCacheReadAccelerationGetByIdSelectionResult>
    > SelectAuthorizedCandidate
)
{
    public ReadableProfileProjectionContext? ReadableProfileProjectionContext { get; init; }

    public ResponseContentCoding ResponseContentCoding { get; init; } = ResponseContentCoding.Identity;
}

public sealed record DocumentCacheReadAccelerationQueryRequest(
    string TenantKey,
    MappingSet MappingSet,
    QualifiedResourceName Resource,
    DocumentCacheReadAccelerationResourceKind ResourceKind,
    Func<CancellationToken, Task<QueryResult>> RelationalFallback,
    Func<
        CancellationToken,
        Task<DocumentCacheReadAccelerationQuerySelectionResult>
    > SelectAuthorizedCandidatePage
)
{
    public ReadableProfileProjectionContext? ReadableProfileProjectionContext { get; init; }

    public ResponseContentCoding ResponseContentCoding { get; init; } = ResponseContentCoding.Identity;
}

internal sealed record DocumentCacheReadLookupResult<TResult>
    where TResult : class
{
    public DocumentCacheReadLookupResult(
        TResult? cachedResult,
        DocumentCacheReadAccelerationFallbackReason fallbackReason,
        IReadOnlyList<DocumentCacheReadAccelerationCandidate>? directFillCandidates = null,
        DocumentCacheReadInvariantDiagnostic? invariantDiagnostic = null,
        DocumentCacheReadLookupOutcome? rawLookupOutcome = null,
        bool isAdapterAcquisitionFailure = false
    )
    {
        CachedResult = cachedResult;
        FallbackReason = fallbackReason;
        DirectFillCandidates = directFillCandidates ?? [];
        InvariantDiagnostic = invariantDiagnostic;
        RawLookupOutcome = rawLookupOutcome is null
            ? null
            : DocumentCacheMaterializerGuards.RequireDefined(
                rawLookupOutcome.Value,
                nameof(rawLookupOutcome),
                "Unsupported DocumentCache read lookup outcome."
            );
        IsAdapterAcquisitionFailure = isAdapterAcquisitionFailure;

        if (CachedResult is null && FallbackReason == DocumentCacheReadAccelerationFallbackReason.None)
        {
            throw new ArgumentException(
                "Fallback results must provide a fallback reason.",
                nameof(fallbackReason)
            );
        }

        if (CachedResult is not null && FallbackReason != DocumentCacheReadAccelerationFallbackReason.None)
        {
            throw new ArgumentException(
                "Cache hit results must not provide a fallback reason.",
                nameof(fallbackReason)
            );
        }

        if (
            IsAdapterAcquisitionFailure
            && RawLookupOutcome != DocumentCacheReadLookupOutcome.CacheUnavailable
        )
        {
            throw new ArgumentException(
                "Adapter acquisition failures must be reported as cache-unavailable lookup outcomes.",
                nameof(isAdapterAcquisitionFailure)
            );
        }
    }

    public TResult? CachedResult { get; }

    public DocumentCacheReadAccelerationFallbackReason FallbackReason { get; }

    public IReadOnlyList<DocumentCacheReadAccelerationCandidate> DirectFillCandidates { get; }

    public DocumentCacheReadInvariantDiagnostic? InvariantDiagnostic { get; }

    public DocumentCacheReadLookupOutcome? RawLookupOutcome { get; }

    public bool IsAdapterAcquisitionFailure { get; }

    public static DocumentCacheReadLookupResult<TResult> Hit(TResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new DocumentCacheReadLookupResult<TResult>(
            result,
            DocumentCacheReadAccelerationFallbackReason.None,
            rawLookupOutcome: DocumentCacheReadLookupOutcome.FreshHit
        );
    }

    public static DocumentCacheReadLookupResult<TResult> Fallback(
        DocumentCacheReadAccelerationFallbackReason reason =
            DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss,
        IReadOnlyList<DocumentCacheReadAccelerationCandidate>? directFillCandidates = null,
        DocumentCacheReadInvariantDiagnostic? invariantDiagnostic = null,
        DocumentCacheReadLookupOutcome? rawLookupOutcome = null,
        bool isAdapterAcquisitionFailure = false
    ) =>
        new(
            null,
            reason,
            directFillCandidates,
            invariantDiagnostic,
            rawLookupOutcome,
            isAdapterAcquisitionFailure
        );

    public static DocumentCacheReadLookupResult<TResult> FallbackFromLookupOutcome(
        DocumentCacheReadLookupOutcome outcome,
        IReadOnlyList<DocumentCacheReadAccelerationCandidate>? directFillCandidates = null,
        DocumentCacheReadInvariantDiagnostic? invariantDiagnostic = null,
        bool isAdapterAcquisitionFailure = false
    ) =>
        Fallback(
            DocumentCacheReadLookupOutcomeMapper.MapFallbackReason(outcome),
            directFillCandidates,
            invariantDiagnostic,
            outcome,
            isAdapterAcquisitionFailure
        );
}

internal sealed record DocumentCacheReadInvariantDiagnostic
{
    private const int MaximumMessageLength = 512;

    public DocumentCacheReadInvariantDiagnostic(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        string sanitizedMessage = LoggingSanitizer.SanitizeForLogging(message);
        Message =
            sanitizedMessage.Length <= MaximumMessageLength
                ? sanitizedMessage
                : sanitizedMessage[..MaximumMessageLength];
    }

    public string Message { get; }

    public static DocumentCacheReadInvariantDiagnostic CacheLookupInvariant() =>
        new("DocumentCache read lookup observed a deterministic cache invariant failure.");

    public static DocumentCacheReadInvariantDiagnostic CacheLookupInvariant(string message) => new(message);

    public static DocumentCacheReadInvariantDiagnostic CacheHitResponseShaping(
        DocumentCacheReadResponseShapingFailureReason reason
    ) =>
        new(
            "DocumentCache cache-hit response shaping observed deterministic failure "
                + $"'{FormatResponseShapingReason(reason)}'."
        );

    public static DocumentCacheReadInvariantDiagnostic DirectFillMaterializerProjectionFailure(
        DocumentCacheProjectionProcessingFailureReason reason
    ) => new($"DocumentCache direct-fill materializer observed deterministic projection failure '{reason}'.");

    public static DocumentCacheReadInvariantDiagnostic DirectFillMaterializerTargetFailure(
        DocumentCacheTargetMappingFailureReason reason
    ) => new($"DocumentCache direct-fill materializer observed target mapping failure '{reason}'.");

    public static DocumentCacheReadInvariantDiagnostic DirectFillWriterFailure(
        DocumentCacheWriterInvariantFailureReason reason
    ) =>
        new(
            $"DocumentCache direct-fill writer observed deterministic invariant or target failure '{reason}'."
        );

    private static string FormatResponseShapingReason(DocumentCacheReadResponseShapingFailureReason reason) =>
        reason switch
        {
            DocumentCacheReadResponseShapingFailureReason.InvalidDocumentJson => "InvalidCachedJson",
            DocumentCacheReadResponseShapingFailureReason.DocumentJsonNotObject => "CachedJsonNotObject",
            DocumentCacheReadResponseShapingFailureReason.DocumentJsonContainsEtag =>
                "CachedJsonContainsServedEtag",
            DocumentCacheReadResponseShapingFailureReason.DocumentJsonIdMismatch => "CachedJsonIdMismatch",
            DocumentCacheReadResponseShapingFailureReason.DocumentJsonLastModifiedDateMismatch =>
                "CachedJsonLastModifiedDateMismatch",
            DocumentCacheReadResponseShapingFailureReason.QueryHitCandidateMismatch => nameof(
                DocumentCacheReadResponseShapingFailureReason.QueryHitCandidateMismatch
            ),
            DocumentCacheReadResponseShapingFailureReason.StreamEtagMismatch => "FixedStreamEtagMismatch",
            _ => "UnsupportedResponseShapingFailure",
        };
}

internal interface IDocumentCacheReadLookupAdapter
{
    Task<DocumentCacheReadLookupResult<GetResult>> TryGetByIdAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate candidateSelection,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheReadLookupResult<QueryResult>> TryQueryAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage candidateSelection,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken = default
    );
}

public interface IDocumentCacheReadAccelerationCoordinator
{
    Task<GetResult> GetByIdAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        CancellationToken cancellationToken = default
    );

    Task<QueryResult> QueryAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed class PassthroughDocumentCacheReadAccelerationCoordinator
    : IDocumentCacheReadAccelerationCoordinator
{
    public static PassthroughDocumentCacheReadAccelerationCoordinator Instance { get; } = new();

    private PassthroughDocumentCacheReadAccelerationCoordinator() { }

    public Task<GetResult> GetByIdAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.RelationalFallback(cancellationToken);
    }

    public Task<QueryResult> QueryAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.RelationalFallback(cancellationToken);
    }
}

internal sealed class DocumentCacheReadAccelerationCoordinator(
    IDataStoreSelection dataStoreSelection,
    IDocumentCacheTargetRegistry targetRegistry,
    IDocumentCacheReadLookupAdapter lookupAdapter,
    IDocumentCacheMaterializer materializer,
    IDocumentCacheWriter cacheWriter,
    IDocumentCacheReadTelemetry readTelemetry,
    IDocumentCacheProjectionTargetDiagnosticSink targetDiagnosticSink,
    TimeProvider timeProvider,
    ILogger<DocumentCacheReadAccelerationCoordinator> logger
) : IDocumentCacheReadAccelerationCoordinator
{
    private readonly IDataStoreSelection _dataStoreSelection =
        dataStoreSelection ?? throw new ArgumentNullException(nameof(dataStoreSelection));
    private readonly IDocumentCacheTargetRegistry _targetRegistry =
        targetRegistry ?? throw new ArgumentNullException(nameof(targetRegistry));
    private readonly IDocumentCacheReadLookupAdapter _lookupAdapter =
        lookupAdapter ?? throw new ArgumentNullException(nameof(lookupAdapter));
    private readonly IDocumentCacheMaterializer _materializer =
        materializer ?? throw new ArgumentNullException(nameof(materializer));
    private readonly IDocumentCacheWriter _cacheWriter =
        cacheWriter ?? throw new ArgumentNullException(nameof(cacheWriter));
    private readonly IDocumentCacheReadTelemetry _readTelemetry =
        readTelemetry ?? throw new ArgumentNullException(nameof(readTelemetry));
    private readonly IDocumentCacheProjectionTargetDiagnosticSink _targetDiagnosticSink =
        targetDiagnosticSink ?? throw new ArgumentNullException(nameof(targetDiagnosticSink));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<DocumentCacheReadAccelerationCoordinator> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private sealed record DirectFillCandidateSelection(
        IReadOnlyList<DocumentCacheReadAccelerationCandidate> Candidates,
        string EmptyCandidateOutcome
    )
    {
        public static DirectFillCandidateSelection For(
            IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates
        ) => new(candidates, DocumentCacheReadTelemetryLabel.SkippedNoCandidates);

        public static DirectFillCandidateSelection Skip(string emptyCandidateOutcome) =>
            new([], emptyCandidateOutcome);
    }

    public async Task<GetResult> GetByIdAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RelationalFallback);
        ArgumentNullException.ThrowIfNull(request.SelectAuthorizedCandidate);

        if (
            !TryPreflightConfiguredTarget(
                request.TenantKey,
                out var configuredTargetFallbackReason,
                out var configuredTargetDirectFillSkipOutcome
            )
        )
        {
            RecordFallback(
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                targetContext: null,
                configuredTargetFallbackReason
            );
            RecordDirectFillSkipIfNeeded(
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                targetContext: null,
                configuredTargetDirectFillSkipOutcome
            );

            return await request.RelationalFallback(cancellationToken).ConfigureAwait(false);
        }

        DocumentCacheReadAccelerationGetByIdSelectionResult selectionResult = await request
            .SelectAuthorizedCandidate(cancellationToken)
            .ConfigureAwait(false);

        if (selectionResult is DocumentCacheReadAccelerationGetByIdSelectionResult.Complete complete)
        {
            return complete.Result;
        }

        var candidateSelection =
            selectionResult as DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate
            ?? throw new InvalidOperationException(
                $"Unsupported GET-by-id candidate selection result '{selectionResult.GetType().Name}'."
            );

        if (
            !TryResolveTarget(
                request.TenantKey,
                out var targetContext,
                out var fallbackReason,
                out var directFillSkipOutcome,
                out var directFillTelemetryTargetContext
            )
        )
        {
            RecordFallback(
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                targetContext: null,
                fallbackReason
            );
            RecordDirectFillSkipIfNeeded(
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                directFillTelemetryTargetContext,
                directFillSkipOutcome
            );

            return await candidateSelection.RelationalFallback(cancellationToken).ConfigureAwait(false);
        }

        DocumentCacheReadLookupResult<GetResult> lookupResult = await LookupGetByIdAsync(
                request,
                candidateSelection,
                targetContext,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (lookupResult.CachedResult is not null)
        {
            RecordHit(
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                targetContext,
                pageHit: false
            );

            return lookupResult.CachedResult;
        }

        RecordLookupFallback(
            DocumentCacheReadAccelerationOperation.GetById,
            request.ResourceKind,
            targetContext,
            lookupResult.FallbackReason,
            lookupResult.RawLookupOutcome,
            lookupResult.IsAdapterAcquisitionFailure,
            lookupResult.InvariantDiagnostic
        );

        GetResult fallbackResult = await candidateSelection
            .RelationalFallback(cancellationToken)
            .ConfigureAwait(false);

        await TryDirectFillAsync(
                request,
                targetContext,
                SelectGetByIdDirectFillCandidates(candidateSelection, lookupResult),
                fallbackResult,
                cancellationToken
            )
            .ConfigureAwait(false);

        return fallbackResult;
    }

    public async Task<QueryResult> QueryAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RelationalFallback);
        ArgumentNullException.ThrowIfNull(request.SelectAuthorizedCandidatePage);

        if (
            !TryPreflightConfiguredTarget(
                request.TenantKey,
                out var configuredTargetFallbackReason,
                out var configuredTargetDirectFillSkipOutcome
            )
        )
        {
            RecordFallback(
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                targetContext: null,
                configuredTargetFallbackReason
            );
            RecordDirectFillSkipIfNeeded(
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                targetContext: null,
                configuredTargetDirectFillSkipOutcome
            );

            return await request.RelationalFallback(cancellationToken).ConfigureAwait(false);
        }

        DocumentCacheReadAccelerationQuerySelectionResult selectionResult = await request
            .SelectAuthorizedCandidatePage(cancellationToken)
            .ConfigureAwait(false);

        if (selectionResult is DocumentCacheReadAccelerationQuerySelectionResult.Complete complete)
        {
            return complete.Result;
        }

        var candidateSelection =
            selectionResult as DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage
            ?? throw new InvalidOperationException(
                $"Unsupported query candidate selection result '{selectionResult.GetType().Name}'."
            );

        if (
            !TryResolveTarget(
                request.TenantKey,
                out var targetContext,
                out var fallbackReason,
                out var directFillSkipOutcome,
                out var directFillTelemetryTargetContext
            )
        )
        {
            RecordFallback(
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                targetContext: null,
                fallbackReason
            );
            RecordDirectFillSkipIfNeeded(
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                directFillTelemetryTargetContext,
                directFillSkipOutcome
            );

            return await candidateSelection.RelationalFallback(cancellationToken).ConfigureAwait(false);
        }

        DocumentCacheReadLookupResult<QueryResult> lookupResult = await LookupQueryAsync(
                request,
                candidateSelection,
                targetContext,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (lookupResult.CachedResult is not null)
        {
            RecordHit(
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                targetContext,
                pageHit: true
            );

            return lookupResult.CachedResult;
        }

        RecordLookupFallback(
            DocumentCacheReadAccelerationOperation.Query,
            request.ResourceKind,
            targetContext,
            lookupResult.FallbackReason,
            lookupResult.RawLookupOutcome,
            lookupResult.IsAdapterAcquisitionFailure,
            lookupResult.InvariantDiagnostic
        );

        QueryResult fallbackResult = await candidateSelection
            .RelationalFallback(cancellationToken)
            .ConfigureAwait(false);

        await TryDirectFillAsync(
                request,
                targetContext,
                SelectQueryDirectFillCandidates(candidateSelection, lookupResult, fallbackResult),
                fallbackResult,
                cancellationToken
            )
            .ConfigureAwait(false);

        return fallbackResult;
    }

    private async Task<DocumentCacheReadLookupResult<GetResult>> LookupGetByIdAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate candidateSelection,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken
    )
    {
        RecordAttempt(DocumentCacheReadAccelerationOperation.GetById, request.ResourceKind, targetContext);
        long lookupStartTimestamp = Stopwatch.GetTimestamp();

        try
        {
            DocumentCacheReadLookupResult<GetResult> result = await _lookupAdapter
                .TryGetByIdAsync(request, candidateSelection, targetContext, cancellationToken)
                .ConfigureAwait(false);

            _readTelemetry.RecordCacheLookupDuration(
                CreateReadTelemetryContext(
                    targetContext,
                    DocumentCacheReadAccelerationOperation.GetById,
                    request.ResourceKind,
                    GetLookupTelemetryOutcome(result, DocumentCacheReadTelemetryLabel.Hit)
                ),
                DocumentCacheReadTelemetry.GetElapsedTime(lookupStartTimestamp)
            );

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordUnexpectedLookupException(
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                targetContext,
                lookupStartTimestamp,
                exception
            );
            throw;
        }
    }

    private async Task<DocumentCacheReadLookupResult<QueryResult>> LookupQueryAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage candidateSelection,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken
    )
    {
        RecordAttempt(DocumentCacheReadAccelerationOperation.Query, request.ResourceKind, targetContext);
        long lookupStartTimestamp = Stopwatch.GetTimestamp();

        try
        {
            DocumentCacheReadLookupResult<QueryResult> result = await _lookupAdapter
                .TryQueryAsync(request, candidateSelection, targetContext, cancellationToken)
                .ConfigureAwait(false);

            _readTelemetry.RecordCacheLookupDuration(
                CreateReadTelemetryContext(
                    targetContext,
                    DocumentCacheReadAccelerationOperation.Query,
                    request.ResourceKind,
                    GetLookupTelemetryOutcome(result, DocumentCacheReadTelemetryLabel.PageHit)
                ),
                DocumentCacheReadTelemetry.GetElapsedTime(lookupStartTimestamp)
            );

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordUnexpectedLookupException(
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                targetContext,
                lookupStartTimestamp,
                exception
            );
            throw;
        }
    }

    private void RecordAttempt(
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        DocumentCacheTargetExecutionContext targetContext
    ) =>
        _readTelemetry.RecordAttempt(
            CreateReadTelemetryContext(
                targetContext,
                operation,
                resourceKind,
                DocumentCacheReadTelemetryLabel.Attempted
            )
        );

    private void RecordHit(
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        DocumentCacheTargetExecutionContext targetContext,
        bool pageHit
    )
    {
        DocumentCacheReadTelemetryContext context = CreateReadTelemetryContext(
            targetContext,
            operation,
            resourceKind,
            pageHit ? DocumentCacheReadTelemetryLabel.PageHit : DocumentCacheReadTelemetryLabel.Hit
        );

        _readTelemetry.RecordHit(context);
        if (pageHit)
        {
            _readTelemetry.RecordPageHit(context);
        }
    }

    private void RecordLookupFallback(
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        DocumentCacheTargetExecutionContext targetContext,
        DocumentCacheReadAccelerationFallbackReason fallbackReason,
        DocumentCacheReadLookupOutcome? rawLookupOutcome,
        bool isAdapterAcquisitionFailure,
        DocumentCacheReadInvariantDiagnostic? invariantDiagnostic = null
    )
    {
        DocumentCacheReadTelemetryContext lookupContext = CreateReadTelemetryContext(
            targetContext,
            operation,
            resourceKind,
            rawLookupOutcome?.ToString() ?? fallbackReason.ToString()
        );
        _readTelemetry.RecordMiss(lookupContext);

        if (fallbackReason == DocumentCacheReadAccelerationFallbackReason.CacheLookupUnavailable)
        {
            _readTelemetry.RecordCacheUnavailable(lookupContext);
            if (isAdapterAcquisitionFailure)
            {
                _readTelemetry.RecordAdapterAcquisitionFailure(lookupContext);
            }
        }

        if (fallbackReason == DocumentCacheReadAccelerationFallbackReason.CacheLookupInvariantFailure)
        {
            RecordTargetInvariantDiagnostic(
                targetContext,
                invariantDiagnostic ?? DocumentCacheReadInvariantDiagnostic.CacheLookupInvariant()
            );
        }

        RecordFallback(operation, resourceKind, targetContext, fallbackReason);
    }

    private void RecordFallback(
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        DocumentCacheTargetExecutionContext? targetContext,
        DocumentCacheReadAccelerationFallbackReason fallbackReason
    )
    {
        DocumentCacheReadTelemetryContext context = CreateReadTelemetryContext(
            targetContext,
            operation,
            resourceKind,
            fallbackReason.ToString()
        );

        _readTelemetry.RecordFallback(context);
    }

    private void RecordUnexpectedLookupException(
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        DocumentCacheTargetExecutionContext targetContext,
        long lookupStartTimestamp,
        Exception exception
    )
    {
        DocumentCacheReadTelemetryContext context = CreateReadTelemetryContext(
            targetContext,
            operation,
            resourceKind,
            DocumentCacheReadTelemetryLabel.UnexpectedException
        );

        _readTelemetry.RecordCacheLookupDuration(
            context,
            DocumentCacheReadTelemetry.GetElapsedTime(lookupStartTimestamp)
        );
        _readTelemetry.RecordUnexpectedException(context);
        _logger.LogWarning(
            exception,
            "DocumentCache read lookup failed unexpectedly for target {TargetKey}. ExceptionType: {ExceptionType}",
            targetContext.TargetKey,
            exception.GetType().Name
        );
    }

    private void RecordDirectFill(
        DocumentCacheTargetExecutionContext? targetContext,
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        string outcome
    ) =>
        _readTelemetry.RecordDirectFill(
            CreateReadTelemetryContext(targetContext, operation, resourceKind, outcome)
        );

    private void RecordDirectFillSkipIfNeeded(
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        DocumentCacheTargetExecutionContext? targetContext,
        string? outcome
    )
    {
        if (outcome is null)
        {
            return;
        }

        RecordDirectFill(targetContext, operation, resourceKind, outcome);
    }

    private static DocumentCacheReadTelemetryContext CreateReadTelemetryContext(
        DocumentCacheTargetExecutionContext? targetContext,
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        string outcome
    ) =>
        targetContext is null
            ? DocumentCacheReadTelemetryContext.ForNoTarget(operation, resourceKind, outcome)
            : DocumentCacheReadTelemetryContext.ForTarget(targetContext, operation, resourceKind, outcome);

    private static string GetLookupTelemetryOutcome<TResult>(
        DocumentCacheReadLookupResult<TResult> lookupResult,
        string hitOutcome
    )
        where TResult : class =>
        lookupResult.CachedResult is not null
            ? hitOutcome
            : lookupResult.RawLookupOutcome?.ToString() ?? lookupResult.FallbackReason.ToString();

    private bool TryPreflightConfiguredTarget(
        string tenantKey,
        out DocumentCacheReadAccelerationFallbackReason fallbackReason,
        out string? directFillSkipOutcome
    )
    {
        directFillSkipOutcome = null;

        if (
            !TryGetSelectedTargetKey(
                tenantKey,
                out _,
                out var targetKey,
                out fallbackReason,
                out directFillSkipOutcome
            )
        )
        {
            return false;
        }

        DocumentCacheTargetObservation? targetObservation = _targetRegistry.CurrentSnapshot.GetTarget(
            targetKey
        );

        if (targetObservation is null)
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.UnresolvedTarget;
            directFillSkipOutcome = DocumentCacheReadTelemetryLabel.SkippedTargetRegistryUnavailable;
            return false;
        }

        if (!targetObservation.EffectiveSettings.ReadAccelerationEnabled)
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.ReadAccelerationDisabled;
            directFillSkipOutcome = DocumentCacheReadTelemetryLabel.SkippedTargetReadAccelerationDisabled;
            return false;
        }

        fallbackReason = DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss;
        return true;
    }

    private bool TryResolveTarget(
        string tenantKey,
        out DocumentCacheTargetExecutionContext targetContext,
        out DocumentCacheReadAccelerationFallbackReason fallbackReason,
        out string? directFillSkipOutcome,
        out DocumentCacheTargetExecutionContext? directFillTelemetryTargetContext
    )
    {
        targetContext = null!;
        directFillSkipOutcome = null;
        directFillTelemetryTargetContext = null;

        if (
            !TryGetSelectedTargetKey(
                tenantKey,
                out var selectedDataStore,
                out var targetKey,
                out fallbackReason,
                out directFillSkipOutcome
            )
        )
        {
            return false;
        }

        DocumentCacheTargetExecutionContext? resolvedTargetContext =
            _targetRegistry.CurrentRuntimeSnapshot.GetExecutionContext(targetKey);

        if (resolvedTargetContext is null)
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.UnresolvedTarget;
            directFillSkipOutcome = DocumentCacheReadTelemetryLabel.SkippedUnresolvedTarget;
            return false;
        }

        if (!resolvedTargetContext.EffectiveSettings.ReadAccelerationEnabled)
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.ReadAccelerationDisabled;
            directFillSkipOutcome = DocumentCacheReadTelemetryLabel.SkippedTargetReadAccelerationDisabled;
            return false;
        }

        // The database this request will actually read. Reaching here means a Primary effective
        // target: the guards above turned away every derivative and every request that never
        // selected a target at all.
        string? readConnectionString = _dataStoreSelection.GetEffectiveTarget().ConnectionString;

        if (!TargetMatchesSelectedDataStore(selectedDataStore, readConnectionString, resolvedTargetContext))
        {
            _logger.LogDebug(
                "DocumentCache read acceleration bypassed for target {TargetKey} because the resolved target signature does not match the selected data store.",
                targetKey
            );

            fallbackReason = DocumentCacheReadAccelerationFallbackReason.UnresolvedTarget;
            directFillSkipOutcome = DocumentCacheReadTelemetryLabel.SkippedTargetMismatch;
            directFillTelemetryTargetContext = resolvedTargetContext;
            return false;
        }

        targetContext = resolvedTargetContext;
        fallbackReason = DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss;
        return true;
    }

    private bool TryGetSelectedTargetKey(
        string tenantKey,
        out DataStore selectedDataStore,
        out DocumentCacheTargetKey targetKey,
        out DocumentCacheReadAccelerationFallbackReason fallbackReason,
        out string? directFillSkipOutcome
    )
    {
        selectedDataStore = null!;
        targetKey = null!;
        directFillSkipOutcome = null;

        // First, before the registry is consulted or a target key is built. The DocumentCache is
        // materialized from, and keyed by, the parent database: a snapshot or replica is a different
        // database whose rows the parent's cache does not describe, and it carries its own
        // dms.DocumentCache that this request must not read, compare against, or fill. Reading the
        // parent's cache for a derivative request would serve one database's documents for another's.
        if (
            _dataStoreSelection.IsEffectiveTargetSet
            && _dataStoreSelection.GetEffectiveTarget().Kind != EffectiveTargetKind.Primary
        )
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.DerivativeTargetSelected;
            directFillSkipOutcome = DocumentCacheReadTelemetryLabel.SkippedDerivativeTarget;
            return false;
        }

        if (!_dataStoreSelection.IsSet)
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.SelectedDataStoreUnavailable;
            directFillSkipOutcome = DocumentCacheReadTelemetryLabel.SkippedSelectedDataStoreUnavailable;
            return false;
        }

        try
        {
            selectedDataStore = _dataStoreSelection.GetSelectedDataStore();
        }
        catch (InvalidOperationException)
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.SelectedDataStoreUnavailable;
            directFillSkipOutcome = DocumentCacheReadTelemetryLabel.SkippedSelectedDataStoreUnavailable;
            return false;
        }

        // Selection is fail-fast by design: every pipeline that resolves a data store also selects an
        // effective target before any database work, and the relational path refuses to read without
        // one. Serving the parent's cache here anyway could hide that missing selection behind a
        // cache hit, so the cache is bypassed and the relational read surfaces the defect instead.
        if (!_dataStoreSelection.IsEffectiveTargetSet)
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.EffectiveTargetNotSelected;
            directFillSkipOutcome = DocumentCacheReadTelemetryLabel.SkippedEffectiveTargetNotSelected;
            return false;
        }

        if (
            !DocumentCacheTargetKey.TryCreate(
                tenantKey,
                selectedDataStore.Id,
                out DocumentCacheTargetKey? createdTargetKey,
                out _
            )
        )
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.InvalidTargetKey;
            directFillSkipOutcome = DocumentCacheReadTelemetryLabel.SkippedInvalidTargetKey;
            return false;
        }

        targetKey = createdTargetKey;
        fallbackReason = DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss;
        return true;
    }

    /// <summary>
    /// Whether the registry's resolved target is the database this request will actually read.
    /// </summary>
    /// <remarks>
    /// Compared against the connection string the request will read rather than the parent's, because
    /// those differ for a derivative and the parent's would match a cache describing a database this
    /// request is not reading. The guard in <see cref="TryGetSelectedTargetKey" /> makes a derivative
    /// unreachable here, so this is the second statement of the same rule rather than the only one.
    /// </remarks>
    private static bool TargetMatchesSelectedDataStore(
        DataStore selectedDataStore,
        string? readConnectionString,
        DocumentCacheTargetExecutionContext targetContext
    ) =>
        selectedDataStore.RelationalProviderToken is not null
        && selectedDataStore.RelationalProviderToken.Equals(targetContext.ProviderToken)
        && string.Equals(readConnectionString, targetContext.ConnectionInput.Value, StringComparison.Ordinal);

    private async Task TryDirectFillAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        DirectFillCandidateSelection candidateSelection,
        GetResult fallbackResult,
        CancellationToken cancellationToken
    )
    {
        if (fallbackResult is not GetResult.GetSuccess success)
        {
            RecordDirectFill(
                targetContext,
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                DocumentCacheReadTelemetryLabel.SkippedFallbackNotSuccessful
            );
            return;
        }

        IReadOnlyList<DocumentCacheReadAccelerationCandidate> survivingCandidates = candidateSelection
            .Candidates.Where(candidate => candidate.DocumentUuid == success.DocumentUuid)
            .ToArray();

        await TryDirectFillAsync(
                request.MappingSet,
                targetContext,
                survivingCandidates,
                candidateSelection.EmptyCandidateOutcome,
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task TryDirectFillAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        DirectFillCandidateSelection candidateSelection,
        QueryResult fallbackResult,
        CancellationToken cancellationToken
    )
    {
        if (fallbackResult is not QueryResult.QuerySuccess success)
        {
            RecordDirectFill(
                targetContext,
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                DocumentCacheReadTelemetryLabel.SkippedFallbackNotSuccessful
            );
            return;
        }

        ISet<Guid> servedDocumentUuids = GetServedDocumentUuids(success.EdfiDocs);
        if (servedDocumentUuids.Count == 0)
        {
            RecordDirectFill(
                targetContext,
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                DocumentCacheReadTelemetryLabel.SkippedNoServedCandidate
            );
            return;
        }

        IReadOnlyList<DocumentCacheReadAccelerationCandidate> survivingCandidates = candidateSelection
            .Candidates.Where(candidate => servedDocumentUuids.Contains(candidate.DocumentUuid.Value))
            .ToArray();

        await TryDirectFillAsync(
                request.MappingSet,
                targetContext,
                survivingCandidates,
                candidateSelection.EmptyCandidateOutcome,
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task TryDirectFillAsync(
        MappingSet mappingSet,
        DocumentCacheTargetExecutionContext targetContext,
        IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates,
        string emptyCandidateOutcome,
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        CancellationToken cancellationToken
    )
    {
        if (candidates.Count == 0)
        {
            RecordDirectFill(targetContext, operation, resourceKind, emptyCandidateOutcome);
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            RecordDirectFill(
                targetContext,
                operation,
                resourceKind,
                DocumentCacheReadTelemetryLabel.SkippedRequestCanceled
            );
            return;
        }

        if (!HasDirectFillStaticTargetRequirements(targetContext))
        {
            RecordDirectFill(
                targetContext,
                operation,
                resourceKind,
                DocumentCacheReadTelemetryLabel.SkippedTargetIneligible
            );
            return;
        }

        long directFillStartTimestamp = Stopwatch.GetTimestamp();
        string directFillDurationOutcome = DocumentCacheReadTelemetryLabel.Completed;
        CancellationTokenSource? directFillTimeout = null;

        try
        {
            TimeSpan timeout = targetContext.EffectiveSettings.DirectFillTimeout;
            directFillTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            directFillTimeout.CancelAfter(timeout);

            var materializationTargetContext = new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey(
                    targetContext.TargetKey.TenantKey,
                    new DataStoreId(targetContext.TargetKey.DataStoreId)
                ),
                mappingSet,
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
                targetContext.ConnectionInput.Value
            );

            foreach (DocumentCacheReadAccelerationCandidate candidate in candidates)
            {
                if (directFillTimeout.IsCancellationRequested)
                {
                    string cancellationOutcome = SelectDirectFillCancellationReason(
                        cancellationToken,
                        directFillTimeout
                    );
                    directFillDurationOutcome = AggregateDirectFillDurationOutcome(
                        directFillDurationOutcome,
                        cancellationOutcome
                    );
                    RecordDirectFill(targetContext, operation, resourceKind, cancellationOutcome);
                    return;
                }

                RecordDirectFill(
                    targetContext,
                    operation,
                    resourceKind,
                    DocumentCacheReadTelemetryLabel.Attempted
                );

                try
                {
                    DocumentCacheMaterializationResult materializationResult = await _materializer
                        .MaterializeAsync(
                            new DocumentCacheMaterializationRequest(
                                materializationTargetContext,
                                candidate.DocumentId,
                                selectedRequiredContentVersion: candidate.ContentVersion,
                                DocumentCacheMaterializationPurpose.DirectFill,
                                directFillTimeout.Token
                            )
                        )
                        .ConfigureAwait(false);

                    if (materializationResult is not DocumentCacheMaterializationResult.Success success)
                    {
                        string materializationOutcome = DocumentCacheReadTelemetryLabel.Failed;
                        directFillDurationOutcome = AggregateDirectFillDurationOutcome(
                            directFillDurationOutcome,
                            materializationOutcome
                        );
                        RecordDirectFill(targetContext, operation, resourceKind, materializationOutcome);
                        continue;
                    }

                    DocumentCacheWriterResult writerResult = await _cacheWriter
                        .WriteAsync(
                            new DocumentCacheWriterRequest(
                                materializationTargetContext,
                                candidate.DocumentId,
                                selectedRequiredContentVersion: success.Candidate.ContentVersion,
                                DocumentCacheWriterPurpose.DirectFill,
                                success.Candidate,
                                directFillTimeout.Token
                            )
                        )
                        .ConfigureAwait(false);

                    if (
                        writerResult
                        is DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure invariantFailure
                    )
                    {
                        RecordTargetInvariantDiagnostic(
                            targetContext,
                            DocumentCacheReadInvariantDiagnostic.DirectFillWriterFailure(
                                invariantFailure.Reason
                            )
                        );
                    }

                    string writerOutcome = IsDirectFillWriterSuccess(writerResult)
                        ? DocumentCacheReadTelemetryLabel.Succeeded
                        : DocumentCacheReadTelemetryLabel.Failed;
                    directFillDurationOutcome = AggregateDirectFillDurationOutcome(
                        directFillDurationOutcome,
                        writerOutcome
                    );
                    RecordDirectFill(targetContext, operation, resourceKind, writerOutcome);
                }
                catch (OperationCanceledException)
                {
                    string cancellationOutcome = SelectDirectFillCancellationReason(
                        cancellationToken,
                        directFillTimeout
                    );
                    directFillDurationOutcome = AggregateDirectFillDurationOutcome(
                        directFillDurationOutcome,
                        cancellationOutcome
                    );
                    RecordDirectFill(targetContext, operation, resourceKind, cancellationOutcome);
                    LogDirectFillSkipped(targetContext, cancellationOutcome);
                    return;
                }
                catch (DocumentCacheProjectionProcessingException exception)
                {
                    string projectionFailureOutcome = DocumentCacheReadTelemetryLabel.Failed;
                    directFillDurationOutcome = AggregateDirectFillDurationOutcome(
                        directFillDurationOutcome,
                        projectionFailureOutcome
                    );
                    RecordDirectFill(targetContext, operation, resourceKind, projectionFailureOutcome);
                    RecordTargetInvariantDiagnostic(
                        targetContext,
                        DocumentCacheReadInvariantDiagnostic.DirectFillMaterializerProjectionFailure(
                            exception.Reason
                        )
                    );
                    LogDirectFillFailure(targetContext, exception, exception.Reason.ToString());
                }
                catch (DocumentCacheTargetMappingException exception)
                {
                    string targetFailureOutcome = DocumentCacheReadTelemetryLabel.Failed;
                    directFillDurationOutcome = AggregateDirectFillDurationOutcome(
                        directFillDurationOutcome,
                        targetFailureOutcome
                    );
                    RecordDirectFill(targetContext, operation, resourceKind, targetFailureOutcome);
                    RecordTargetInvariantDiagnostic(
                        targetContext,
                        DocumentCacheReadInvariantDiagnostic.DirectFillMaterializerTargetFailure(
                            exception.Reason
                        )
                    );
                    LogDirectFillFailure(targetContext, exception, exception.Reason.ToString());
                }
                catch (Exception exception)
                {
                    string unexpectedFailureOutcome = DocumentCacheReadTelemetryLabel.Failed;
                    directFillDurationOutcome = AggregateDirectFillDurationOutcome(
                        directFillDurationOutcome,
                        unexpectedFailureOutcome
                    );
                    RecordDirectFill(targetContext, operation, resourceKind, unexpectedFailureOutcome);
                    LogDirectFillFailure(
                        targetContext,
                        exception,
                        DocumentCacheReadTelemetryLabel.UnexpectedException
                    );
                }
            }
        }
        catch (OperationCanceledException)
        {
            string cancellationOutcome = SelectDirectFillCancellationReason(
                cancellationToken,
                directFillTimeout
            );
            directFillDurationOutcome = AggregateDirectFillDurationOutcome(
                directFillDurationOutcome,
                cancellationOutcome
            );
            RecordDirectFill(targetContext, operation, resourceKind, cancellationOutcome);
            LogDirectFillSkipped(targetContext, cancellationOutcome);
        }
        catch (Exception exception)
        {
            string setupFailureOutcome = DocumentCacheReadTelemetryLabel.Failed;
            directFillDurationOutcome = AggregateDirectFillDurationOutcome(
                directFillDurationOutcome,
                setupFailureOutcome
            );
            RecordDirectFill(targetContext, operation, resourceKind, setupFailureOutcome);
            LogDirectFillFailure(
                targetContext,
                exception,
                DocumentCacheReadTelemetryLabel.UnexpectedException
            );
        }
        finally
        {
            directFillTimeout?.Dispose();
            _readTelemetry.RecordDirectFillDuration(
                CreateReadTelemetryContext(targetContext, operation, resourceKind, directFillDurationOutcome),
                DocumentCacheReadTelemetry.GetElapsedTime(directFillStartTimestamp)
            );
        }
    }

    private void RecordTargetInvariantDiagnostic(
        DocumentCacheTargetExecutionContext targetContext,
        DocumentCacheReadInvariantDiagnostic invariantDiagnostic
    )
    {
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        DocumentCacheTargetDiagnostic targetDiagnostic = new(
            targetContext.TargetKey,
            DocumentCacheTargetResolutionState.Resolved,
            targetContext.ProviderToken,
            targetContext.Generation,
            physicalSourceFingerprint: null,
            lifecycle: null,
            inventory: null,
            enqueueTrigger: null,
            sqlServerPrerequisites: null,
            retryState: null,
            DocumentCacheTargetDiagnosticCategory.DeterministicInvariantFailure,
            invariantDiagnostic.Message
        );

        _targetDiagnosticSink.AppendTargetDiagnostic(
            new DocumentCacheProjectionTargetContextKey(targetContext.TargetKey, targetContext.Generation),
            targetDiagnostic,
            observedAt
        );
    }

    private static DirectFillCandidateSelection SelectGetByIdDirectFillCandidates(
        DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate candidateSelection,
        DocumentCacheReadLookupResult<GetResult> lookupResult
    )
    {
        if (lookupResult.IsAdapterAcquisitionFailure)
        {
            return DirectFillCandidateSelection.Skip(DocumentCacheReadTelemetryLabel.SkippedCacheUnavailable);
        }

        if (IsLiveRebuildingLookupOutcome(lookupResult.RawLookupOutcome))
        {
            return DirectFillCandidateSelection.For([candidateSelection.AuthorizedCandidate]);
        }

        return IsGetByIdLiveTrackingDirectFillOutcome(lookupResult.RawLookupOutcome)
            ? DirectFillCandidateSelection.For(lookupResult.DirectFillCandidates)
            : DirectFillCandidateSelection.Skip(DocumentCacheReadTelemetryLabel.SkippedNoCandidates);
    }

    private static DirectFillCandidateSelection SelectQueryDirectFillCandidates(
        DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage candidateSelection,
        DocumentCacheReadLookupResult<QueryResult> lookupResult,
        QueryResult fallbackResult
    )
    {
        if (lookupResult.IsAdapterAcquisitionFailure)
        {
            return DirectFillCandidateSelection.Skip(DocumentCacheReadTelemetryLabel.SkippedCacheUnavailable);
        }

        if (fallbackResult is not QueryResult.QuerySuccess)
        {
            return DirectFillCandidateSelection.Skip(DocumentCacheReadTelemetryLabel.SkippedNoCandidates);
        }

        if (IsLiveRebuildingLookupOutcome(lookupResult.RawLookupOutcome))
        {
            return DirectFillCandidateSelection.For(candidateSelection.AuthorizedCandidatePage.Candidates);
        }

        return IsQueryLiveTrackingDirectFillOutcome(lookupResult.RawLookupOutcome)
            ? DirectFillCandidateSelection.For(lookupResult.DirectFillCandidates)
            : DirectFillCandidateSelection.Skip(DocumentCacheReadTelemetryLabel.SkippedNoCandidates);
    }

    private static bool HasDirectFillStaticTargetRequirements(
        DocumentCacheTargetExecutionContext targetContext
    ) =>
        targetContext.Inventory.Status == DocumentCacheInventoryStatus.Satisfied
        && targetContext.SqlServerPrerequisites?.HasFailure != true;

    private static bool IsLiveRebuildingLookupOutcome(DocumentCacheReadLookupOutcome? rawLookupOutcome) =>
        rawLookupOutcome == DocumentCacheReadLookupOutcome.LifecycleRebuilding;

    private static bool IsGetByIdLiveTrackingDirectFillOutcome(
        DocumentCacheReadLookupOutcome? rawLookupOutcome
    ) =>
        rawLookupOutcome
            is DocumentCacheReadLookupOutcome.MissingCacheRow
                or DocumentCacheReadLookupOutcome.StaleCacheRow
                or DocumentCacheReadLookupOutcome.SourceDrift;

    private static bool IsQueryLiveTrackingDirectFillOutcome(
        DocumentCacheReadLookupOutcome? rawLookupOutcome
    ) =>
        rawLookupOutcome
            is DocumentCacheReadLookupOutcome.MissingCacheRow
                or DocumentCacheReadLookupOutcome.MissingSourceRow
                or DocumentCacheReadLookupOutcome.StaleCacheRow
                or DocumentCacheReadLookupOutcome.SourceDrift;

    private static ISet<Guid> GetServedDocumentUuids(JsonArray edfiDocs)
    {
        HashSet<Guid> documentUuids = [];

        foreach (JsonNode? edfiDoc in edfiDocs)
        {
            if (
                edfiDoc is JsonObject jsonObject
                && jsonObject.TryGetPropertyValue("id", out JsonNode? idNode)
                && idNode is JsonValue idValue
                && idValue.TryGetValue(out string? id)
                && Guid.TryParse(id, out Guid documentUuid)
            )
            {
                documentUuids.Add(documentUuid);
            }
        }

        return documentUuids;
    }

    private static bool IsDirectFillWriterSuccess(DocumentCacheWriterResult result) =>
        result
            is DocumentCacheWriterResult.CandidateWrittenAcknowledged
                or DocumentCacheWriterResult.AlreadyCurrentAcknowledged
                or DocumentCacheWriterResult.AlreadyCurrentNoWork;

    private static string AggregateDirectFillDurationOutcome(
        string currentOutcome,
        string candidateOutcome
    ) =>
        GetDirectFillDurationOutcomePriority(candidateOutcome)
        > GetDirectFillDurationOutcomePriority(currentOutcome)
            ? candidateOutcome
            : currentOutcome;

    private static int GetDirectFillDurationOutcomePriority(string outcome) =>
        outcome switch
        {
            DocumentCacheReadTelemetryLabel.CallerCanceled => 5,
            DocumentCacheReadTelemetryLabel.TimedOut => 4,
            DocumentCacheReadTelemetryLabel.Canceled => 3,
            DocumentCacheReadTelemetryLabel.Failed => 2,
            DocumentCacheReadTelemetryLabel.Succeeded => 1,
            _ => 0,
        };

    private static string SelectDirectFillCancellationReason(
        CancellationToken requestCancellationToken,
        CancellationTokenSource? directFillTimeout
    )
    {
        if (requestCancellationToken.IsCancellationRequested)
        {
            return DocumentCacheReadTelemetryLabel.CallerCanceled;
        }

        return directFillTimeout?.IsCancellationRequested == true
            ? DocumentCacheReadTelemetryLabel.TimedOut
            : DocumentCacheReadTelemetryLabel.Canceled;
    }

    private void LogDirectFillSkipped(DocumentCacheTargetExecutionContext targetContext, string reason) =>
        _logger.LogDebug(
            "DocumentCache direct fill stopped for target {TargetKey}. Reason: {Reason}",
            targetContext.TargetKey,
            reason
        );

    private void LogDirectFillFailure(
        DocumentCacheTargetExecutionContext targetContext,
        Exception exception,
        string failureReason
    ) =>
        _logger.LogWarning(
            "DocumentCache direct fill failed for target {TargetKey}. FailureReason: {FailureReason}. ExceptionType: {ExceptionType}",
            targetContext.TargetKey,
            failureReason,
            exception.GetType().Name
        );
}
